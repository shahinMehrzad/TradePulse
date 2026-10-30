namespace Application.Interfaces;

public sealed record UserSummary(Guid Id, string UserName, string Email, IReadOnlyList<string> Roles, bool TwoFactorEnabled, bool MustChangePassword, bool IsDisabled);

public sealed record CreatedUser(UserSummary? User, string? TemporaryPassword, IReadOnlyList<string> Errors);

public enum UserChangeOutcome { Done, NotFound, Self, LastAdministrator }

/// <summary>What an administrator may do to the accounts of the panel.</summary>
public interface IUserAdministration
{
    Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken);

    /// <summary>Creates an administrator with a generated one-time password; the user has to change it and enrol two-factor at the first sign-in.</summary>
    Task<CreatedUser> CreateAdministratorAsync(string userName, string email, CancellationToken cancellationToken);

    /// <summary>Gives the user a new generated one-time password, which has to be changed at the next sign-in.</summary>
    Task<(UserChangeOutcome Outcome, string? TemporaryPassword)> ResetPasswordAsync(Guid userId, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>Forgets the user's authenticator and recovery codes; they enrol again at the next sign-in.</summary>
    Task<UserChangeOutcome> ResetTwoFactorAsync(Guid userId, Guid actingUserId, CancellationToken cancellationToken);

    Task<UserChangeOutcome> SetDisabledAsync(Guid userId, bool disabled, Guid actingUserId, CancellationToken cancellationToken);
}
