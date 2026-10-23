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

        var user = await identityService.FindByIdAsync(userId.Value, cancellationToken)
                   ?? throw new UnauthorizedAccessException("Invalid token.");

        var token = accessTokenService.Create(user);
        return new AuthResponseDto(token.Token, token.ExpiresAt, user.UserName, user.Id, token.RefreshTokenDurationInDays);
    }
}
