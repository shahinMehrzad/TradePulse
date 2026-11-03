using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Identity;

/// <summary>
/// <see cref="ILoginThrottle"/> on the auth schema. A wrong attempt reads its row with <c>FOR UPDATE</c> inside a transaction, so two
/// simultaneous guesses are counted one after the other and cannot both slip under the limit; the first wrong attempt of a kind (no row yet)
/// is made safe by the unique index and one retry.
/// </summary>
public sealed class LoginThrottleService(AuthDbContext context, TimeProvider time) : ILoginThrottle
{
    private static readonly TimeSpan[] AccountWideLocks = [LoginThrottlePolicy.AccountWideLock];

    /// <summary>Rows with nothing left to say are removed after this long without a wrong attempt.</summary>
    private static readonly TimeSpan KeepUnlocked = TimeSpan.FromDays(8);

    private DateTime Now => time.GetUtcNow().UtcDateTime;

    public async Task<DateTime?> PasswordLockedUntilAsync(Guid userId, string address, CancellationToken cancellationToken)
    {
        var now = Now;
        return await context.LoginThrottles.AsNoTracking()
            .Where(t => t.UserId == userId && t.LockedUntil > now
                        && ((t.Kind == LoginThrottleKind.PasswordFromAddress && t.Address == address) || t.Kind == LoginThrottleKind.PasswordForAccount))
            .MaxAsync(t => t.LockedUntil, cancellationToken);
    }

    public async Task<PasswordFailure> PasswordFailedAsync(Guid userId, string address, CancellationToken cancellationToken) => new(
        await FailAsync(userId, LoginThrottleKind.PasswordFromAddress, address, LoginThrottlePolicy.PasswordAttempts,
            LoginThrottlePolicy.PasswordWindow, LoginThrottlePolicy.PasswordLocks, cancellationToken),
        await FailAsync(userId, LoginThrottleKind.PasswordForAccount, string.Empty, LoginThrottlePolicy.AccountWideAttempts,
            LoginThrottlePolicy.AccountWideWindow, AccountWideLocks, cancellationToken));

    public async Task PasswordSucceededAsync(Guid userId, string address, CancellationToken cancellationToken) =>
        await context.LoginThrottles
            .Where(t => t.UserId == userId && t.Kind == LoginThrottleKind.PasswordFromAddress && t.Address == address)
            .ExecuteDeleteAsync(cancellationToken);

    public async Task<DateTime?> TwoFactorLockedUntilAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = Now;
        return await context.LoginThrottles.AsNoTracking()
            .Where(t => t.UserId == userId && t.Kind == LoginThrottleKind.TwoFactor && t.LockedUntil > now)
            .MaxAsync(t => t.LockedUntil, cancellationToken);
    }

    public Task<ThrottleOutcome> TwoFactorFailedAsync(Guid userId, CancellationToken cancellationToken) =>
        FailAsync(userId, LoginThrottleKind.TwoFactor, string.Empty, LoginThrottlePolicy.TwoFactorAttempts,
            LoginThrottlePolicy.TwoFactorWindow, LoginThrottlePolicy.TwoFactorLocks, cancellationToken);

    public async Task TwoFactorSucceededAsync(Guid userId, CancellationToken cancellationToken) =>
        await context.LoginThrottles.Where(t => t.UserId == userId && t.Kind == LoginThrottleKind.TwoFactor).ExecuteDeleteAsync(cancellationToken);

    public async Task ClearAsync(Guid userId, CancellationToken cancellationToken) =>
        await context.LoginThrottles.Where(t => t.UserId == userId).ExecuteDeleteAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, AccountLock>> ActiveLocksAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var active = await context.LoginThrottles.AsNoTracking().Where(t => t.LockedUntil > now).ToListAsync(cancellationToken);

        return active.GroupBy(t => t.UserId).ToDictionary(
            group => group.Key,
            group => new AccountLock(
                group.Where(t => t.Kind != LoginThrottleKind.PasswordFromAddress).Max(t => t.LockedUntil),
                group.Count(t => t.Kind == LoginThrottleKind.PasswordFromAddress)));
    }

    public async Task CleanUpAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var old = now - KeepUnlocked;
        await context.LoginThrottles.Where(t => (t.LockedUntil == null || t.LockedUntil < now) && t.UpdatedAt < old).ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>Counts one wrong attempt; locks when it is the one that reaches <paramref name="attempts"/>.</summary>
    private async Task<ThrottleOutcome> FailAsync(
        Guid userId, LoginThrottleKind kind, string address, int attempts, TimeSpan window, TimeSpan[] locks, CancellationToken cancellationToken)
    {
        for (var tries = 0; ; tries++)
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var now = Now;

            var rows = await context.LoginThrottles
                .FromSqlInterpolated($"""SELECT * FROM auth.login_throttles WHERE "UserId" = {userId} AND "Kind" = {(int)kind} AND "Address" = {address} FOR UPDATE""")
                .ToListAsync(cancellationToken);
            var row = rows.SingleOrDefault();
            var isNew = row is null;
            if (row is null)
            {
                row = new LoginThrottle { UserId = userId, Kind = kind, Address = address };
                context.LoginThrottles.Add(row);
            }

            // Forgotten: attempts older than the window, and rounds that nobody added to for a week.
            if (row.FirstFailureAt is { } first && now - first > window)
            {
                row.Failures = 0;
                row.FirstFailureAt = null;
            }

            if (row.LockedUntil is { } ended && ended + LoginThrottlePolicy.RoundsForgottenAfter < now)
                row.Rounds = 0;

            // Closed already: a guess that got in anyway (two at once) is not counted again, and does not make the lock longer.
            if (row.LockedUntil > now)
                return new ThrottleOutcome(row.Failures, row.Rounds, row.LockedUntil, JustLocked: false);

            row.Failures++;
            row.FirstFailureAt ??= now;
            row.UpdatedAt = now;

            var justLocked = false;
            if (row.Failures >= attempts)
            {
                row.Rounds++;
                row.LockedUntil = now + LoginThrottlePolicy.LockFor(locks, row.Rounds);
                row.Failures = 0;
                row.FirstFailureAt = null;
                justLocked = true;
            }

            try
            {
                await context.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new ThrottleOutcome(row.Failures, row.Rounds, row.LockedUntil, justLocked);
            }
            catch (DbUpdateException) when (isNew && tries == 0)
            {
                // Another request made the row a moment ago (the unique index said no): count again on top of theirs.
                context.ChangeTracker.Clear();
            }
        }
    }
}
