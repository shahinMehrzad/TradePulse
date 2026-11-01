using Application.Dtos.Auth;
using Application.Interfaces;
using Application.Services;

namespace Application.Commands.Auth;

/// <summary>Step one: the password. It never returns tokens by itself, only what the user has to do next.</summary>
public class LoginCommandHandler(IIdentityService identityService, LoginSteps steps)
    : ICommandHandler<LoginCommand, LoginStepDto>
{
    public async Task<LoginStepDto> HandleAsync(LoginCommand request, CancellationToken cancellationToken)
    {
        var result = await identityService.CheckCredentialsAsync(request.Email, request.Username, request.Password, cancellationToken);

        // A locked or disabled account answers like a wrong password: a stranger learns nothing about which accounts exist.
        if (result.Outcome != CredentialsOutcome.Valid)
            throw new UnauthorizedAccessException("The email/username or password is incorrect.");

        return steps.Next(result.Account!);
    }
}
