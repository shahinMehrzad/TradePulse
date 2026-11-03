namespace Domain.Entities;

/// <summary>What a throttle counts: it is one row per user and kind (and address, for the first kind).</summary>
public enum LoginThrottleKind
{
    /// <summary>Wrong passwords for one account from ONE address. The lock closes that address for that account, nobody else.</summary>
    PasswordFromAddress = 1,

    /// <summary>Wrong passwords for one account from ALL addresses together: a guess spread over many addresses.</summary>
    PasswordForAccount = 2,

    /// <summary>Wrong two-factor codes after the right password. For the whole account: the one who has the password can come from anywhere.</summary>
    TwoFactor = 3
}

/// <summary>
/// The wrong attempts of one kind against one account, and the lock they led to. A lock ends by itself, and every round of wrong attempts that
/// follows a lock locks for longer (there is no lock that only a person can lift: the only administrator must never be shut out for good).
/// Times are UTC, stored as <c>timestamp</c> (no zone) like everywhere else.
/// </summary>
public class LoginThrottle
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    public LoginThrottleKind Kind { get; set; }

    /// <summary>The client address for <see cref="LoginThrottleKind.PasswordFromAddress"/>; empty for the other kinds.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Wrong attempts since the last lock or success, inside the window.</summary>
    public int Failures { get; set; }

    /// <summary>When the first of those attempts was made: the window runs from here.</summary>
    public DateTime? FirstFailureAt { get; set; }

    /// <summary>How many locks in a row (no success between): it picks how long the next one is.</summary>
    public int Rounds { get; set; }

    public DateTime? LockedUntil { get; set; }

    public DateTime UpdatedAt { get; set; }
}
