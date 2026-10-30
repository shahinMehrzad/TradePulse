using Application.Dtos.Auth;
using Application.Interfaces;
using Application.Services;

namespace Application.Commands.Auth;

public record VerifyTwoFactorCommand(string ChallengeToken, string Code, bool IsRecoveryCode);

/// <summary>Last step of a normal sign-in: the code from the authenticator app (or a recovery code).</summary>
public class VerifyTwoFactorCommandHandler(IIdentityService identityService, LoginSteps steps)
    : ICommandHandler<VerifyTwoFactorCommand, LoginStepDto>
{
    public async Task<LoginStepDto> HandleAsync(VerifyTwoFactorCommand request, CancellationToken cancellationToken)
    {
        var account = await steps.AccountOfAsync(request.ChallengeToken, identityService, cancellationToken);
        if (account.NeedsSetup)
            throw new UnauthorizedAccessException(LoginSteps.ExpiredMessage);

        var outcome = await identityService.VerifyTwoFactorAsync(account.User.Id, request.Code, request.IsRecoveryCode, cancellationToken);
        return outcome switch
        {
            TwoFactorOutcome.Valid => steps.Authenticated(account),
            TwoFactorOutcome.LockedOut => throw new UnauthorizedAccessException("Too many wrong codes. Try again later."),
            _ => throw new UnauthorizedAccessException("The code is incorrect.")
        };
    }
}
