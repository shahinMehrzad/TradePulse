namespace Application.Interfaces;

public sealed record ElevationToken(string Token, DateTime ExpiresAt);

/// <summary>
/// A short-lived proof that the signed-in user has just entered a second-factor code ("step-up"). Sensitive actions (managing
/// users, later the exchange keys and trading) ask for it in the <c>X-Elevation</c> header on top of the access token, so a stolen
/// session alone is not enough. It names one user, is not an access token, and a refresh does not renew it.
/// </summary>
public interface IElevationTokenService
{
    ElevationToken Create(Guid userId);

    /// <returns>The user the token was issued to, or null when it is missing, invalid or expired.</returns>
    Guid? Validate(string? token);
}
