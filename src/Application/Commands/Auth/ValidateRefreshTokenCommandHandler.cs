using Application.Dtos.Auth;
using Application.Interfaces;
using Application.Services;
using Domain.Repositories;

namespace Application.Commands.Auth;

public class ValidateRefreshTokenCommandHandler(
    IRefreshTokenRepository refreshTokenRepository,
    IIdentityService identityService,
    IAccessTokenService accessTokenService) : ICommandHandler<ValidateRefreshTokenCommand, AuthResponseDto>
{
    public async Task<AuthResponseDto> HandleAsync(ValidateRefreshTokenCommand request, CancellationToken cancellationToken)
    {
        // The refresh token alone identifies the user: the access token is usually expired by the time this is called.
        var userId = await refreshTokenRepository.ConsumeAsync(RefreshTokenHasher.Hash(request.RefreshToken), request.DeviceId, cancellationToken);
        if (userId is null)
            throw new UnauthorizedAccessException("Invalid token.");

        // A disabled user, or one who has to set up again (password or two-factor reset), gets no new session from an old token.
        var account = await identityService.FindActiveAccountAsync(userId.Value, cancellationToken);
        if (account is null || account.NeedsSetup)
            throw new UnauthorizedAccessException("Invalid token.");

        var token = accessTokenService.Create(account.User);
        return new AuthResponseDto(token.Token, token.ExpiresAt, account.User.UserName, account.User.Id, token.RefreshTokenDurationInDays);
    }
}
