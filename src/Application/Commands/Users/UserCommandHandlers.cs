using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.Users;

public readonly record struct CreateAdministratorCommand(string UserName, string Email);

public readonly record struct ResetUserPasswordCommand(Guid UserId, Guid ActingUserId);

public readonly record struct ResetUserTwoFactorCommand(Guid UserId, Guid ActingUserId);

public readonly record struct SetUserDisabledCommand(Guid UserId, bool Disabled, Guid ActingUserId);

/// <summary>The one-time password is shown to the administrator once and never stored in readable form.</summary>
public sealed record TemporaryPasswordDto(Guid UserId, string UserName, string TemporaryPassword);

internal static class UserChanges
{
    /// <summary>Turns the refusals of <see cref="IUserAdministration"/> into the exceptions the API answers with.</summary>
    public static void ThrowUnlessDone(UserChangeOutcome outcome)
    {
        switch (outcome)
        {
            case UserChangeOutcome.NotFound:
                throw new ConfigurationNotFoundException("That user does not exist (anymore).");
            case UserChangeOutcome.Self:
                throw new ConfigurationConflictException("You cannot do that to your own account.");
            case UserChangeOutcome.LastAdministrator:
                throw new ConfigurationConflictException("That is the last active administrator.");
        }
    }
}

public sealed class CreateAdministratorCommandHandler(IUserAdministration users, AuditTrail audit)
    : ICommandHandler<CreateAdministratorCommand, TemporaryPasswordDto>
{
    public async Task<TemporaryPasswordDto> HandleAsync(CreateAdministratorCommand request, CancellationToken cancellationToken)
    {
        var userName = request.UserName?.Trim() ?? string.Empty;
        var email = request.Email?.Trim() ?? string.Empty;
        if (userName.Length == 0 || email.Length == 0)
            throw new ConfigurationValidationException("User name and email are required.");

        var created = await users.CreateAdministratorAsync(userName, email, cancellationToken);
        if (created.User is null || created.TemporaryPassword is null)
            throw new ConfigurationValidationException(string.Join(" ", created.Errors));

        await audit.RecordAsync(AuditAction.Added, AuditEntityTypes.User, 0, created.User.UserName, $"administrator, email {created.User.Email}", cancellationToken);
        return new TemporaryPasswordDto(created.User.Id, created.User.UserName, created.TemporaryPassword);
    }
}

public sealed class ResetUserPasswordCommandHandler(IUserAdministration users, IRefreshTokenRepository refreshTokens, AuditTrail audit)
    : ICommandHandler<ResetUserPasswordCommand, TemporaryPasswordDto>
{
    public async Task<TemporaryPasswordDto> HandleAsync(ResetUserPasswordCommand request, CancellationToken cancellationToken)
    {
        var (outcome, password) = await users.ResetPasswordAsync(request.UserId, request.ActingUserId, cancellationToken);
        UserChanges.ThrowUnlessDone(outcome);

        await refreshTokens.RevokeAllAsync(request.UserId, cancellationToken);
        var user = (await users.ListAsync(cancellationToken)).First(u => u.Id == request.UserId);
        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, user.UserName, "reset the password", cancellationToken);
        return new TemporaryPasswordDto(user.Id, user.UserName, password!);
    }
}

public sealed class ResetUserTwoFactorCommandHandler(IUserAdministration users, IRefreshTokenRepository refreshTokens, AuditTrail audit)
    : ICommandHandler<ResetUserTwoFactorCommand, bool>
{
    public async Task<bool> HandleAsync(ResetUserTwoFactorCommand request, CancellationToken cancellationToken)
    {
        UserChanges.ThrowUnlessDone(await users.ResetTwoFactorAsync(request.UserId, request.ActingUserId, cancellationToken));

        await refreshTokens.RevokeAllAsync(request.UserId, cancellationToken);
        var user = (await users.ListAsync(cancellationToken)).First(u => u.Id == request.UserId);
        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, user.UserName, "reset two-factor authentication", cancellationToken);
        return true;
    }
}

public sealed class SetUserDisabledCommandHandler(IUserAdministration users, IRefreshTokenRepository refreshTokens, AuditTrail audit)
    : ICommandHandler<SetUserDisabledCommand, bool>
{
    public async Task<bool> HandleAsync(SetUserDisabledCommand request, CancellationToken cancellationToken)
    {
        UserChanges.ThrowUnlessDone(await users.SetDisabledAsync(request.UserId, request.Disabled, request.ActingUserId, cancellationToken));

        if (request.Disabled)
            await refreshTokens.RevokeAllAsync(request.UserId, cancellationToken);

        var user = (await users.ListAsync(cancellationToken)).First(u => u.Id == request.UserId);
        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, user.UserName, request.Disabled ? "disabled" : "enabled", cancellationToken);
        return true;
    }
}

/// <summary>The administrators and other users of the panel, for the user page.</summary>
public readonly record struct ListUsersQuery;

public sealed class ListUsersQueryHandler(IUserAdministration users) : ICommandHandler<ListUsersQuery, IReadOnlyList<UserSummary>>
{
    public Task<IReadOnlyList<UserSummary>> HandleAsync(ListUsersQuery request, CancellationToken cancellationToken) =>
        users.ListAsync(cancellationToken);
}
