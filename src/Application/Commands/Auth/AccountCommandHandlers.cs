using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.Auth;

public readonly record struct GetAccountQuery(Guid UserId);

public readonly record struct ChangeOwnPasswordCommand(Guid UserId, string CurrentPassword, string NewPassword, string? DeviceId);

public readonly record struct RegenerateRecoveryCodesCommand(Guid UserId, string Password);

/// <summary>What a signed-in user may see about their own account. No secret is in it.</summary>
/// <param name="EmailAvailable">The server can send mail, so email codes can be turned on.</param>
public sealed record AccountDto(string UserName, string Email, IReadOnlyList<string> Roles, bool TwoFactorEnabled, int RecoveryCodesLeft, bool EmailCodesEnabled, bool EmailAvailable);

public sealed class GetAccountQueryHandler(IIdentityService identityService, IMailSender mail) : ICommandHandler<GetAccountQuery, AccountDto>
{
    public async Task<AccountDto> HandleAsync(GetAccountQuery request, CancellationToken cancellationToken)
    {
        var account = await identityService.FindActiveAccountAsync(request.UserId, cancellationToken)
                      ?? throw new UnauthorizedAccessException("The account does not exist.");
        var left = account.TwoFactorEnabled ? await identityService.CountRecoveryCodesAsync(request.UserId, cancellationToken) : 0;
        return new AccountDto(account.User.UserName, account.User.Email, account.User.Roles, account.TwoFactorEnabled, left, account.EmailCodesEnabled, mail.IsConfigured);
    }
}

/// <summary>A user changes their own password. Every other device is signed out; the one they are using stays.</summary>
public sealed class ChangeOwnPasswordCommandHandler(IIdentityService identityService, IRefreshTokenRepository refreshTokens, AuditTrail audit)
    : ICommandHandler<ChangeOwnPasswordCommand, bool>
{
    public async Task<bool> HandleAsync(ChangeOwnPasswordCommand request, CancellationToken cancellationToken)
    {
        if (request.NewPassword == request.CurrentPassword)
            throw new ConfigurationValidationException("The new password must differ from the current one.");

        var result = await identityService.ChangePasswordAsync(request.UserId, request.CurrentPassword, request.NewPassword, cancellationToken);
        if (!result.Succeeded)
            throw new ConfigurationValidationException(string.Join(" ", result.Errors));

        await refreshTokens.RevokeAllAsync(request.UserId, cancellationToken, exceptDeviceId: string.IsNullOrEmpty(request.DeviceId) ? null : request.DeviceId);
        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, await NameAsync(request.UserId, cancellationToken), "changed the password", cancellationToken);
        return true;
    }

    private async Task<string> NameAsync(Guid userId, CancellationToken cancellationToken) =>
        (await identityService.FindActiveAccountAsync(userId, cancellationToken))?.User.UserName ?? userId.ToString();
}

/// <summary>New recovery codes replace the old ones, which stop working. The password is asked again because the codes can bypass the app.</summary>
public sealed class RegenerateRecoveryCodesCommandHandler(IIdentityService identityService, AuditTrail audit)
    : ICommandHandler<RegenerateRecoveryCodesCommand, IReadOnlyList<string>>
{
    public async Task<IReadOnlyList<string>> HandleAsync(RegenerateRecoveryCodesCommand request, CancellationToken cancellationToken)
    {
        var codes = await identityService.RegenerateRecoveryCodesAsync(request.UserId, request.Password, cancellationToken)
                    ?? throw new ConfigurationValidationException("The password is incorrect, or two-factor authentication is off.");

        var name = (await identityService.FindActiveAccountAsync(request.UserId, cancellationToken))?.User.UserName ?? request.UserId.ToString();
        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, name, "made new recovery codes", cancellationToken);
        return codes;
    }
}
