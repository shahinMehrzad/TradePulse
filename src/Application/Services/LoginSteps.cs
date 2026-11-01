using Application.Dtos.Auth;
using Application.Interfaces;

namespace Application.Services;

/// <summary>Decides what a user who got past one step of the sign-in sees next, so every handler agrees on the order.</summary>
public sealed class LoginSteps(IAccessTokenService accessTokens, IChallengeTokenService challenges, IMailSender mail)
{
    public const string ExpiredMessage = "The sign-in has expired. Sign in again.";

    public LoginStepDto Next(Account account)
    {
        var challenge = challenges.Create(account.User.Id).Token;
        return account.NeedsSetup
            ? new LoginStepDto(LoginStage.SetupRequired, null, challenge, account.MustChangePassword, !account.TwoFactorEnabled)
            : new LoginStepDto(LoginStage.TwoFactorRequired, null, challenge, false, false, EmailCodeAvailable: account.EmailCodesEnabled && mail.IsConfigured);
    }

    public AuthResponseDto Session(Account account)
    {
        var token = accessTokens.Create(account.User);
        return new AuthResponseDto(token.Token, token.ExpiresAt, account.User.UserName, account.User.Id, token.RefreshTokenDurationInDays);
    }

    public LoginStepDto Authenticated(Account account) => new(LoginStage.Authenticated, Session(account), null, false, false);

    /// <summary>The account behind a challenge token.</summary>
    /// <exception cref="UnauthorizedAccessException">The token is invalid or expired, or the user is gone or disabled.</exception>
    public async Task<Account> AccountOfAsync(string challengeToken, IIdentityService identity, CancellationToken cancellationToken)
    {
        var userId = challenges.Validate(challengeToken) ?? throw new UnauthorizedAccessException(ExpiredMessage);
        return await identity.FindActiveAccountAsync(userId, cancellationToken) ?? throw new UnauthorizedAccessException(ExpiredMessage);
    }
}
