using Application.Interfaces;
using Application.Notifications;
using Domain.Entities;

namespace Application.Services;

/// <summary>
/// The rule of "too many wrong guesses" in one place: asks <see cref="ILoginThrottle"/> whether a sign-in step is closed, counts the wrong
/// ones, and when a lock closes (or a wrong code comes twice in a row) writes the audit log and sends the alert. The places that check a
/// password or a code only ask this and say what happened.
/// </summary>
public sealed class LoginGuard(ILoginThrottle throttle, ISecurityAlerts alerts, AuditTrail audit, IClientAddress client, TimeProvider time)
{
    /// <summary>The audit log entries of these locks are made by nobody who is signed in: the system.</summary>
    public const string AuditUserName = "system";

    public async Task<bool> PasswordLockedAsync(Guid userId, CancellationToken cancellationToken) =>
        await throttle.PasswordLockedUntilAsync(userId, client.Address, cancellationToken) is not null;

    public async Task PasswordFailedAsync(Guid userId, string userName, CancellationToken cancellationToken)
    {
        var failure = await throttle.PasswordFailedAsync(userId, client.Address, cancellationToken);

        if (failure.FromAddress.JustLocked)
        {
            var until = failure.FromAddress.LockedUntil!.Value;
            await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, userName,
                $"locked {client.Address} out of the account for {Length(until)} after {LoginThrottlePolicy.PasswordAttempts} wrong passwords (round {failure.FromAddress.Round})",
                cancellationToken, AuditUserName);

            // The first lock is most often a person who typed it wrong; from the second round on it is somebody who keeps trying.
            if (failure.FromAddress.Round >= 2)
                alerts.Raise(new SecurityAlert($"password:{userId}", $"Wrong passwords for {userName}",
                    $"The address {client.Address} typed {LoginThrottlePolicy.PasswordAttempts} wrong passwords again after its last lock (round {failure.FromAddress.Round}). " +
                    $"It is locked out of this account until {Utc(until)}."));
        }

        if (failure.AccountWide.JustLocked)
        {
            var until = failure.AccountWide.LockedUntil!.Value;
            await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, userName,
                $"locked the account for {Length(until)} after {LoginThrottlePolicy.AccountWideAttempts} wrong passwords within an hour from several addresses",
                cancellationToken, AuditUserName);
            alerts.Raise(new SecurityAlert($"password-wide:{userId}", $"Many wrong passwords for {userName}",
                $"{LoginThrottlePolicy.AccountWideAttempts} wrong passwords for this account within an hour, from several addresses (the last: {client.Address}). " +
                $"The account takes no password until {Utc(until)}."));
        }
    }

    public Task PasswordSucceededAsync(Guid userId, CancellationToken cancellationToken) =>
        throttle.PasswordSucceededAsync(userId, client.Address, cancellationToken);

    public async Task<bool> TwoFactorLockedAsync(Guid userId, CancellationToken cancellationToken) =>
        await throttle.TwoFactorLockedUntilAsync(userId, cancellationToken) is not null;

    /// <returns>True when the account is locked now (this wrong code closed it).</returns>
    public async Task<bool> TwoFactorFailedAsync(Guid userId, string userName, CancellationToken cancellationToken)
    {
        var outcome = await throttle.TwoFactorFailedAsync(userId, cancellationToken);

        if (outcome.JustLocked)
        {
            var until = outcome.LockedUntil!.Value;
            await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, userName,
                $"locked the account for {Length(until)} after {LoginThrottlePolicy.TwoFactorAttempts} wrong two-factor codes (round {outcome.Round})",
                cancellationToken, AuditUserName);
            alerts.Raise(new SecurityAlert($"two-factor-lock:{userId}", $"Two-factor locked for {userName}",
                $"{LoginThrottlePolicy.TwoFactorAttempts} wrong two-factor codes after the right password (round {outcome.Round}), the last from {client.Address}. " +
                $"The account takes no code until {Utc(until)}. Whoever did this has the password: reset it."));
            return true;
        }

        if (outcome.Failures == LoginThrottlePolicy.TwoFactorAlertAt)
            alerts.Raise(new SecurityAlert($"two-factor-code:{userId}", $"Wrong two-factor code for {userName}",
                $"The second wrong code in a row after the right password, from {client.Address}. " +
                "If that was not them, somebody else has the password."));

        return outcome.LockedUntil is { } locked && locked > time.GetUtcNow().UtcDateTime;
    }

    public Task TwoFactorSucceededAsync(Guid userId, CancellationToken cancellationToken) =>
        throttle.TwoFactorSucceededAsync(userId, cancellationToken);

    private string Length(DateTime until)
    {
        var span = until - time.GetUtcNow().UtcDateTime;
        return span.TotalHours >= 2 ? $"{Math.Round(span.TotalHours)} hours" : $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} minutes";
    }

    private static string Utc(DateTime until) => $"{until:yyyy-MM-dd HH:mm} UTC";
}
