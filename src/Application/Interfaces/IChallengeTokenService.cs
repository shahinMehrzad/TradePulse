namespace Application.Interfaces;

public sealed record ChallengeToken(string Token, DateTime ExpiresAt);

/// <summary>
/// A short-lived proof that the password was right, handed out between the steps of a sign-in (change the password, enrol or enter the
/// second factor). It is not an access token: the API's bearer validation rejects it, and it opens nothing but those steps.
/// </summary>
public interface IChallengeTokenService
{
    ChallengeToken Create(Guid userId);

    /// <returns>The user the token was issued to, or null when it is invalid or expired.</returns>
    Guid? Validate(string token);
}
