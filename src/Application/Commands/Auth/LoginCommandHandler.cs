using Application.Dtos.Auth;
using Application.Interfaces;

namespace Application.Commands.Auth;

public class LoginCommandHandler(IIdentityService identityService, IAccessTokenService accessTokenService)
    : ICommandHandler<LoginCommand, AuthResponseDto>
{
    public async Task<AuthResponseDto> HandleAsync(LoginCommand request, CancellationToken cancellationToken)
    {
        var user = await identityService.CheckCredentialsAsync(request.Email, request.Username, request.Password, cancellationToken)
                   ?? throw new UnauthorizedAccessException("The email/username or password is incorrect.");

        var token = accessTokenService.Create(user);
        return new AuthResponseDto(token.Token, token.ExpiresAt, user.UserName, user.Id, token.RefreshTokenDurationInDays);
    }
}
