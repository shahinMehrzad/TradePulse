using Application.Dtos.Auth;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;

namespace Application.Commands.Auth;

public record BeginTwoFactorEnrollmentCommand(string ChallengeToken);

public record ConfirmTwoFactorEnrollmentCommand(string ChallengeToken, string Code);

/// <summary>Setup step: gives the key (the panel draws the QR code from it) for the authenticator app.</summary>
public class BeginTwoFactorEnrollmentCommandHandler(IIdentityService identityService, LoginSteps steps)
    : ICommandHandler<BeginTwoFactorEnrollmentCommand, TwoFactorEnrollmentDto>
{
    public async Task<TwoFactorEnrollmentDto> HandleAsync(BeginTwoFactorEnrollmentCommand request, CancellationToken cancellationToken)
    {
        var account = await steps.AccountOfAsync(request.ChallengeToken, identityService, cancellationToken);
        if (account.MustChangePassword)
            throw new ConfigurationValidationException("Change the password first.");

        var enrollment = await identityService.BeginTwoFactorEnrollmentAsync(account.User.Id, cancellationToken)
                         ?? throw new ConfigurationConflictException("Two-factor authentication is already on.");
        return new TwoFactorEnrollmentDto(enrollment.SharedKey, enrollment.AuthenticatorUri);
    }
}

/// <summary>Setup step: the first code proves the app has the key. Two-factor turns on and the user is signed in.</summary>
public class ConfirmTwoFactorEnrollmentCommandHandler(IIdentityService identityService, LoginSteps steps, AuditTrail audit)
    : ICommandHandler<ConfirmTwoFactorEnrollmentCommand, TwoFactorEnrolledDto>
{
    public async Task<TwoFactorEnrolledDto> HandleAsync(ConfirmTwoFactorEnrollmentCommand request, CancellationToken cancellationToken)
    {
        var account = await steps.AccountOfAsync(request.ChallengeToken, identityService, cancellationToken);
        if (account.MustChangePassword)
            throw new ConfigurationValidationException("Change the password first.");

        var recoveryCodes = await identityService.ConfirmTwoFactorEnrollmentAsync(account.User.Id, request.Code, cancellationToken)
                            ?? throw new ConfigurationValidationException("The code is incorrect. Check the app and try the next code.");

        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, account.User.UserName, "turned on two-factor authentication",
            cancellationToken, userName: account.User.UserName);

        return new TwoFactorEnrolledDto(steps.Session(account), recoveryCodes);
    }
}
