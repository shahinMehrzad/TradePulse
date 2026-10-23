namespace Application.Interfaces;

public sealed record AccessToken(string Token, DateTime ExpiresAt, int RefreshTokenDurationInDays);

public interface IAccessTokenService
{
    AccessToken Create(AuthenticatedUser user);
}
