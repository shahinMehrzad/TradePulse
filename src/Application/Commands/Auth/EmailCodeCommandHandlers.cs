using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;

namespace Application.Commands.Auth;

public readonly record struct SendEmailCodeCommand(string ChallengeToken);

public readonly record struct SendOwnEmailCodeCommand(Guid UserId);

public readonly record struct EnableEmailCodesCommand(Guid UserId, string Code);

public readonly record struct DisableEmailCodesCommand(Guid UserId, string Password);

/// <summary>At the code step of a sign-in: mail a code to the user who turned email codes on.</summary>
public sealed class SendEmailCodeCommandHandler(IIdentityService identityService, LoginSteps steps, EmailCodeSender sender)
    : ICommandHandler<SendEmailCodeCommand, bool>
{
    public async Task<bool> HandleAsync(SendEmailCodeCommand request, CancellationToken cancellationToken)
    {
        var account = await steps.AccountOfAsync(request.ChallengeToken, identityService, cancellationToken);
        if (account.NeedsSetup)
            throw new UnauthorizedAccessException(LoginSteps.ExpiredMessage);

        await sender.SendAsync(account.User.Id, forEnabling: false, cancellationToken);
        return true;
    }
}

/// <summary>Signed in and turning email codes on: mail the test code.</summary>
public sealed class SendOwnEmailCodeCommandHandler(EmailCodeSender sender) : ICommandHandler<SendOwnEmailCodeCommand, bool>
{
    public async Task<bool> HandleAsync(SendOwnEmailCodeCommand request, CancellationToken cancellationToken)
    {
        await sender.SendAsync(request.UserId, forEnabling: true, cancellationToken);
        return true;
    }
}

/// <summary>The test code came back: email codes are on, and the address is confirmed.</summary>
public sealed class EnableEmailCodesCommandHandler(IIdentityService identityService, AuditTrail audit) : ICommandHandler<EnableEmailCodesCommand, bool>
{
    public async Task<bool> HandleAsync(EnableEmailCodesCommand request, CancellationToken cancellationToken)
    {
        if (!await identityService.EnableEmailCodesAsync(request.UserId, request.Code.Trim(), cancellationToken))
            throw new ConfigurationValidationException("The code is incorrect or has expired. Ask for a new one.");

        var name = (await identityService.FindActiveAccountAsync(request.UserId, cancellationToken))?.User.UserName ?? request.UserId.ToString();
        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, name, "turned on sign-in codes by email", cancellationToken);
        return true;
    }
}

public sealed class DisableEmailCodesCommandHandler(IIdentityService identityService, AuditTrail audit) : ICommandHandler<DisableEmailCodesCommand, bool>
{
    public async Task<bool> HandleAsync(DisableEmailCodesCommand request, CancellationToken cancellationToken)
    {
        if (!await identityService.DisableEmailCodesAsync(request.UserId, request.Password, cancellationToken))
            throw new ConfigurationValidationException("The password is incorrect.");

        var name = (await identityService.FindActiveAccountAsync(request.UserId, cancellationToken))?.User.UserName ?? request.UserId.ToString();
        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, name, "turned off sign-in codes by email", cancellationToken);
        return true;
    }
}
