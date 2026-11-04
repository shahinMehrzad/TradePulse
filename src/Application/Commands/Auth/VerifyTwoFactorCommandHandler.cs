using Application.Dtos.Auth;
using Application.Interfaces;
using Application.Services;

namespace Application.Commands.Auth;

public record VerifyTwoFactorCommand(string ChallengeToken, string Code, TwoFactorMethod Method = TwoFactorMethod.Authenticator);

/// <summary>
/// Last step of a normal sign-in: the code from the authenticator app (or a recovery code). A code from the app or by email also counts as the
/// step-up for the next 10 minutes (it would be refused as used if the user tried to enter it again); a recovery code does not.
/// </summary>
public class VerifyTwoFactorCommandHandler(IIdentityService identityService, LoginSteps steps, IElevationTokenService elevation)
    : ICommandHandler<VerifyTwoFactorCommand, LoginStepDto>
{
    public async Task<LoginStepDto> HandleAsync(VerifyTwoFactorCommand request, CancellationToken cancellationToken)
    {
        var account = await steps.AccountOfAsync(request.ChallengeToken, identityService, cancellationToken);
        if (account.NeedsSetup)
            throw new UnauthorizedAccessException(LoginSteps.ExpiredMessage);

        var outcome = await identityService.VerifyTwoFactorAsync(account.User.Id, request.Code, request.Method, cancellationToken);
        return outcome switch
        {
            TwoFactorOutcome.Valid => steps.Authenticated(account) with { Elevation = request.Method == TwoFactorMethod.Recovery ? null : Elevate(account) },
            TwoFactorOutcome.LockedOut => throw new UnauthorizedAccessException("Too many wrong codes. Try again later."),
            TwoFactorOutcome.AlreadyUsed => throw new UnauthorizedAccessException("That code was already used. Wait for the next one (it changes every 30 seconds)."),
            _ => throw new UnauthorizedAccessException("The code is incorrect.")
        };
    }

    private ElevationDto Elevate(Account account)
    {
        var token = elevation.Create(account.User.Id);
        return new ElevationDto(token.Token, token.ExpiresAt);
    }
}
