namespace Application.Services;

/// <summary>
/// How much wrong guessing is allowed and how long it is locked for. No setting changes these: they are the rule, and the tests
/// check them with a clock they can move. A lock is always temporary and each round is longer than the one before it.
/// </summary>
public static class LoginThrottlePolicy
{
    /// <summary>Wrong passwords for an account from one address before that address is locked out of that account.</summary>
    public const int PasswordAttempts = 5;

    public static readonly TimeSpan[] PasswordLocks =
        [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(6), TimeSpan.FromHours(24)];

    /// <summary>Attempts older than this are forgotten (a typo today and a typo next month are not the same guess).</summary>
    public static readonly TimeSpan PasswordWindow = TimeSpan.FromHours(24);

    /// <summary>Wrong passwords for one account from all addresses together, inside <see cref="AccountWideWindow"/>: a guess spread over many addresses.</summary>
    public const int AccountWideAttempts = 30;

    public static readonly TimeSpan AccountWideWindow = TimeSpan.FromHours(1);

    /// <summary>Short on purpose: this lock closes the account for the real owner too.</summary>
    public static readonly TimeSpan AccountWideLock = TimeSpan.FromMinutes(15);

    /// <summary>Wrong codes after the right password before the account is locked. Whoever gets this far has the password, so it is harder.</summary>
    public const int TwoFactorAttempts = 5;

    public static readonly TimeSpan[] TwoFactorLocks =
        [TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24)];

    public static readonly TimeSpan TwoFactorWindow = TimeSpan.FromHours(24);

    /// <summary>The wrong code that sends a message: the first one can be a slip, the second in a row is worth a look.</summary>
    public const int TwoFactorAlertAt = 2;

    /// <summary>A round that is older than this is forgotten: somebody who locked themselves out last month starts from the shortest lock.</summary>
    public static readonly TimeSpan RoundsForgottenAfter = TimeSpan.FromDays(7);

    /// <summary>The length of lock number <paramref name="round"/> (1 for the first); the last one of the list is the longest there is.</summary>
    public static TimeSpan LockFor(TimeSpan[] schedule, int round) => schedule[Math.Clamp(round, 1, schedule.Length) - 1];
}
