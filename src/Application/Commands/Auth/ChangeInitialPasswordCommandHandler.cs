using Application.Dtos.Auth;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.Auth;

public record ChangeInitialPasswordCommand(string ChallengeToken, string CurrentPassword, string NewPassword);

/// <summary>Setup step: replaces the seeded or admin-given password. Sessions the old password opened are ended.</summary>
public class ChangeInitialPasswordCommandHandler(
    IIdentityService identityService,
    IRefreshTokenRepository refreshTokens,
    LoginSteps steps,
    AuditTrail audit) : ICommandHandler<ChangeInitialPasswordCommand, LoginStepDto>
{
    public async Task<LoginStepDto> HandleAsync(ChangeInitialPasswordCommand request, CancellationToken cancellationToken)
    {
        var account = await steps.AccountOfAsync(request.ChallengeToken, identityService, cancellationToken);
        if (!account.MustChangePassword)
            throw new ConfigurationValidationException("The password does not have to be changed.");
        if (request.NewPassword == request.CurrentPassword)
            throw new ConfigurationValidationException("The new password must differ from the current one.");

        var result = await identityService.ChangePasswordAsync(account.User.Id, request.CurrentPassword, request.NewPassword, cancellationToken);
        if (!result.Succeeded)
            throw new ConfigurationValidationException(string.Join(" ", result.Errors));

        await refreshTokens.RevokeAllAsync(account.User.Id, cancellationToken);
        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, account.User.UserName, "changed the password",
            cancellationToken, userName: account.User.UserName);

        var changed = await identityService.FindActiveAccountAsync(account.User.Id, cancellationToken)
                      ?? throw new UnauthorizedAccessException(LoginSteps.ExpiredMessage);
        return steps.Next(changed);
    }
}
