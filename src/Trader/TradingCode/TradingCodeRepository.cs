using Dapper;
using Npgsql;
using Trader.Vault;

namespace Trader.TradingCode;

/// <summary>How much wrong guessing of the trading code is allowed. The same shape as the sign-in code of the API: a lock ends by itself and each round is longer.</summary>
public static class TradingCodePolicy
{
    public const int Attempts = 5;

    public static readonly TimeSpan[] Locks = [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24)];

    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public static readonly TimeSpan RoundsForgottenAfter = TimeSpan.FromDays(7);

    /// <summary>The wrong code that sends a message: the first can be a slip.</summary>
    public const int AlertAt = 2;

    public const int RecoveryCodeCount = 10;

    public static TimeSpan LockFor(int round) => Locks[Math.Clamp(round, 1, Locks.Length) - 1];
}

/// <param name="Enrolled">False while the user has seen the QR code but not yet proved the app has it.</param>
public sealed record TradingCodeRow(
    Guid OwnerUserId, byte[] SecretEncrypted, bool Enrolled, long LastStep, string? RecoveryHashes, int Failures, DateTime? FirstFailureAt, int Rounds,
    DateTime? LockedUntil, DateTime CreatedAt, DateTime? EnrolledAt);

/// <param name="JustLocked">This wrong code was the one that closed the lock.</param>
public sealed record FailureOutcome(int Failures, int Round, DateTime? LockedUntil, bool JustLocked);

/// <summary>
/// The trading code of each user (schema <c>vault</c>, in the vault's own database): the secret, encrypted with the master key, the codes
/// that were already used (as the last step accepted), the recovery codes (as hashes) and the count of wrong guesses. Times are <c>timestamp</c>, always UTC.
/// </summary>
public sealed class TradingCodeRepository(NpgsqlDataSource dataSource, VaultCrypto crypto)
{
    /// <summary>Part of what the encryption covers, next to the owner: a row of another table cannot be put in its place.</summary>
    private static readonly Guid SecretId = new("5ec2e7c0-7c0d-4e5f-9a31-0f0c0de0c0de");

    private const string Columns = """
        owner_user_id AS OwnerUserId, secret_encrypted AS SecretEncrypted, enrolled AS Enrolled, last_step AS LastStep, recovery_hashes AS RecoveryHashes,
        failures AS Failures, first_failure_at AS FirstFailureAt, rounds AS Rounds, locked_until AS LockedUntil, created_at AS CreatedAt, enrolled_at AS EnrolledAt
        """;

    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS vault;
            CREATE TABLE IF NOT EXISTS vault.trading_codes (
                owner_user_id    uuid PRIMARY KEY,
                secret_encrypted bytea NOT NULL,
                enrolled         boolean NOT NULL,
                last_step        bigint NOT NULL DEFAULT 0,
                recovery_hashes  text NULL,
                failures         integer NOT NULL DEFAULT 0,
                first_failure_at timestamp NULL,
                rounds           integer NOT NULL DEFAULT 0,
                locked_until     timestamp NULL,
                created_at       timestamp NOT NULL,
                enrolled_at      timestamp NULL
            );
            """, cancellationToken: cancellationToken));
    }

    private static DateTime Unspecified(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

    private static TradingCodeRow AsUtc(TradingCodeRow row) => row with
    {
        FirstFailureAt = row.FirstFailureAt is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null,
        LockedUntil = row.LockedUntil is { } l ? DateTime.SpecifyKind(l, DateTimeKind.Utc) : null,
        CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
        EnrolledAt = row.EnrolledAt is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null
    };

    public byte[] SecretOf(TradingCodeRow row) => crypto.Decrypt(row.SecretEncrypted, SecretId, row.OwnerUserId);

    public async Task<TradingCodeRow?> FindAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TradingCodeRow>(new CommandDefinition(
            $"SELECT {Columns} FROM vault.trading_codes WHERE owner_user_id = @owner", new { owner }, cancellationToken: cancellationToken));
        return row is null ? null : AsUtc(row);
    }

    /// <summary>
    /// The secret of a user who is not enrolled yet: made now, or the one made before (a reloaded page shows the key the app may already hold).
    /// Null when the user is enrolled: a code that exists is not replaced here.
    /// </summary>
    public async Task<(byte[] Secret, bool Created)?> BeginAsync(Guid owner, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (await FindAsync(owner, cancellationToken) is { } existing)
            return existing.Enrolled ? null : (SecretOf(existing), false);

        var secret = Totp.NewSecret();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var inserted = await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO vault.trading_codes (owner_user_id, secret_encrypted, enrolled, created_at) VALUES (@owner, @secret, false, @at)
            ON CONFLICT (owner_user_id) DO NOTHING
            """, new { owner, secret = crypto.Encrypt(secret, SecretId, owner), at = Unspecified(nowUtc) }, cancellationToken: cancellationToken));

        // Two first requests at the same moment: the second finds the row of the first and shows its key.
        return inserted == 1 ? (secret, true) : await BeginAsync(owner, nowUtc, cancellationToken);
    }

    /// <summary>Takes the step of a right code, once: false when that step (or a later one) was accepted already.</summary>
    public async Task<bool> TryAcceptStepAsync(Guid owner, long step, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE vault.trading_codes SET last_step = @step WHERE owner_user_id = @owner AND last_step < @step",
            new { owner, step }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task CompleteEnrollmentAsync(Guid owner, IReadOnlyList<string> recoveryHashes, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE vault.trading_codes
            SET enrolled = true, enrolled_at = @at, recovery_hashes = @hashes, failures = 0, first_failure_at = NULL, rounds = 0, locked_until = NULL
            WHERE owner_user_id = @owner
            """, new { owner, at = Unspecified(nowUtc), hashes = string.Join(';', recoveryHashes) }, cancellationToken: cancellationToken));
    }

    /// <summary>Removes the code of the user: the next sign-in to the trading pages starts the setup again.</summary>
    public async Task<bool> DeleteAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM vault.trading_codes WHERE owner_user_id = @owner", new { owner }, cancellationToken: cancellationToken)) == 1;
    }

    public async Task<IReadOnlyList<(Guid Owner, bool Enrolled, DateTime? EnrolledAt)>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<(Guid, bool, DateTime?)>(new CommandDefinition(
            "SELECT owner_user_id, enrolled, enrolled_at FROM vault.trading_codes ORDER BY created_at", cancellationToken: cancellationToken))).ToList();
    }

    /// <summary>Uses up a recovery code: true when it was one of the user's remaining ones.</summary>
    public async Task<bool> ConsumeRecoveryCodeAsync(Guid owner, string hash, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var stored = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT recovery_hashes FROM vault.trading_codes WHERE owner_user_id = @owner AND enrolled FOR UPDATE", new { owner }, transaction, cancellationToken: cancellationToken));

        var remaining = (stored ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (!remaining.Remove(hash))
            return false;

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE vault.trading_codes SET recovery_hashes = @hashes WHERE owner_user_id = @owner",
            new { owner, hashes = string.Join(';', remaining) }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> RecoveryCodesLeftAsync(Guid owner, CancellationToken cancellationToken) =>
        ((await FindAsync(owner, cancellationToken))?.RecoveryHashes ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries).Length;

    // ---- wrong guesses

    public async Task<DateTime?> LockedUntilAsync(Guid owner, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var row = await FindAsync(owner, cancellationToken);
        return row?.LockedUntil is { } until && until > nowUtc ? until : null;
    }

    /// <summary>Counts one wrong code (or wrong recovery code) and locks, for longer each round, at the fifth.</summary>
    public async Task<FailureOutcome> RegisterFailureAsync(Guid owner, DateTime nowUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<TradingCodeRow>(new CommandDefinition(
            $"SELECT {Columns} FROM vault.trading_codes WHERE owner_user_id = @owner FOR UPDATE", new { owner }, transaction, cancellationToken: cancellationToken));
        if (row is null)
            return new FailureOutcome(0, 0, null, false);
        row = AsUtc(row);

        var failures = row.Failures;
        var first = row.FirstFailureAt;
        var rounds = row.Rounds;
        if (first is { } started && nowUtc - started > TradingCodePolicy.Window)
        {
            failures = 0;
            first = null;
        }

        if (row.LockedUntil is { } ended && ended + TradingCodePolicy.RoundsForgottenAfter < nowUtc)
            rounds = 0;

        // Locked already: a guess that got in at the same moment is not counted again and does not lengthen the lock.
        if (row.LockedUntil > nowUtc)
            return new FailureOutcome(row.Failures, row.Rounds, row.LockedUntil, false);

        failures++;
        first ??= nowUtc;
        DateTime? lockedUntil = row.LockedUntil;
        var justLocked = false;
        if (failures >= TradingCodePolicy.Attempts)
        {
            rounds++;
            lockedUntil = nowUtc + TradingCodePolicy.LockFor(rounds);
            failures = 0;
            first = null;
            justLocked = true;
        }

        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE vault.trading_codes SET failures = @failures, first_failure_at = @first, rounds = @rounds, locked_until = @locked WHERE owner_user_id = @owner
            """,
            new { owner, failures, first = first is { } f ? Unspecified(f) : (DateTime?)null, rounds, locked = lockedUntil is { } l ? Unspecified(l) : (DateTime?)null },
            transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return new FailureOutcome(failures, rounds, lockedUntil, justLocked);
    }

    /// <summary>A right code: the count of wrong ones and the rounds start over.</summary>
    public async Task ClearFailuresAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE vault.trading_codes SET failures = 0, first_failure_at = NULL, rounds = 0, locked_until = NULL WHERE owner_user_id = @owner",
            new { owner }, cancellationToken: cancellationToken));
    }
}
