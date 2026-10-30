using Application.Interfaces;
using Application.Services;
using Domain.Exceptions;

namespace Application.Commands.Auth;

public readonly record struct StepUpCommand(Guid UserId, string Code, TwoFactorMethod Method);

public readonly record struct SendStepUpEmailCodeCommand(Guid UserId);

public sealed record ElevationDto(string Token, DateTime ExpiresAt);

/// <summary>
/// A signed-in user proves again, with a code, that it is them. Only the authenticator app and email codes count: a recovery code is
/// for getting in when the phone is lost, not for confirming an action.
/// </summary>
public sealed class StepUpCommandHandler(IIdentityService identityService, IElevationTokenService elevation)
    : ICommandHandler<StepUpCommand, ElevationDto>
{
    public async Task<ElevationDto> HandleAsync(StepUpCommand request, CancellationToken cancellationToken)
    {
        if (request.Method == TwoFactorMethod.Recovery)
            throw new ConfigurationValidationException("Use the code of your authenticator app or a code sent by email.");

        var account = await identityService.FindActiveAccountAsync(request.UserId, cancellationToken)
                      ?? throw new UnauthorizedAccessException("The account does not exist.");
        if (account.NeedsSetup)
            throw new ConfigurationValidationException("Finish setting up the account first.");

        var outcome = await identityService.VerifyTwoFactorAsync(request.UserId, request.Code.Trim(), request.Method, cancellationToken);
        switch (outcome)
        {
            case TwoFactorOutcome.LockedOut:
                throw new ConfigurationConflictException("Too many wrong codes. Try again later.");
            case not TwoFactorOutcome.Valid:
                throw new ConfigurationValidationException("The code is incorrect.");
        }

        var token = elevation.Create(request.UserId);
        return new ElevationDto(token.Token, token.ExpiresAt);
    }
}

/// <summary>Mails a code for a step-up to a user who turned email codes on.</summary>
public sealed class SendStepUpEmailCodeCommandHandler(EmailCodeSender sender) : ICommandHandler<SendStepUpEmailCodeCommand, bool>
{
    public async Task<bool> HandleAsync(SendStepUpEmailCodeCommand request, CancellationToken cancellationToken)
    {
        await sender.SendAsync(request.UserId, forEnabling: false, cancellationToken);
        return true;
    }
}
