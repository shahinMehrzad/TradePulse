namespace Domain.Entities;

/// <summary>
/// A two-factor code that was accepted once. A code is good for a minute or two, and the standard check does not mind it being used again in
/// that time, so somebody who saw it over a shoulder could use it too. The code is kept (as a hash with the user, for a few minutes) so that it
/// works exactly once. The unique index on (user, code) is what makes two uses at the same moment safe.
/// </summary>
public class UsedTwoFactorCode
{
    public long Id { get; set; }

    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the user and the code. Not a secret of any weight (six digits, a few minutes), but it is not kept in the open either.</summary>
    public string CodeHash { get; set; } = string.Empty;

    public DateTime UsedAt { get; set; }
}
