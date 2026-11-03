namespace Application.Interfaces;

/// <summary>What one wrong attempt did to a throttle. <see cref="JustLocked"/> is true for the attempt that closed it.</summary>
public sealed record ThrottleOutcome(int Failures, int Round, DateTime? LockedUntil, bool JustLocked);

/// <summary>A wrong password counts against the address it came from and against the account as a whole.</summary>
public sealed record PasswordFailure(ThrottleOutcome FromAddress, ThrottleOutcome AccountWide);

/// <param name="LockedUntil">The end of the lock that closes the whole account (wrong codes, or a guess from many addresses); null when there is none.</param>
/// <param name="LockedAddresses">How many addresses are locked out of the account right now.</param>
public sealed record AccountLock(DateTime? LockedUntil, int LockedAddresses);

/// <summary>
/// Counts the wrong passwords and wrong codes of every account and says when a sign-in has to be refused. Identity's own lockout is not used
/// for this: it is one count for the whole account and one fixed time, so anybody who knew a user name could keep an administrator out.
/// Here a wrong password locks the address it came from (and, from many addresses at once, the account for a short time), a wrong code locks
/// the account, and every round of a lock is longer. Nothing here is permanent.
/// </summary>
public interface ILoginThrottle
{
    /// <returns>When the lock on this address (or on the account as a whole) ends, or null when a password may be tried.</returns>
    Task<DateTime?> PasswordLockedUntilAsync(Guid userId, string address, CancellationToken cancellationToken);

    Task<PasswordFailure> PasswordFailedAsync(Guid userId, string address, CancellationToken cancellationToken);

    /// <summary>The right password: this address starts from nothing again (what is counted against the account as a whole stays).</summary>
    Task PasswordSucceededAsync(Guid userId, string address, CancellationToken cancellationToken);

    Task<DateTime?> TwoFactorLockedUntilAsync(Guid userId, CancellationToken cancellationToken);

    Task<ThrottleOutcome> TwoFactorFailedAsync(Guid userId, CancellationToken cancellationToken);

    Task TwoFactorSucceededAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Forgets every count and lock of the user (an administrator unlocked them, or their password or two-factor was reset).</summary>
    Task ClearAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The locks in force now, by user.</summary>
    Task<IReadOnlyDictionary<Guid, AccountLock>> ActiveLocksAsync(CancellationToken cancellationToken);

    /// <summary>Removes what has nothing left to say: no lock in force and no attempt in the last days.</summary>
    Task CleanUpAsync(CancellationToken cancellationToken);
}
