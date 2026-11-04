namespace Application.Interfaces;

/// <param name="LockedUntil">Until when the account takes no password or code because of wrong guesses (UTC); null when it is not locked. Not the same as disabled.</param>
/// <param name="LockedAddresses">How many addresses are locked out of the account for wrong passwords.</param>
public sealed record UserSummary(
    Guid Id, string UserName, string Email, IReadOnlyList<string> Roles, bool TwoFactorEnabled, bool MustChangePassword, bool IsDisabled,
    DateTime? LockedUntil = null, int LockedAddresses = 0);

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

    /// <summary>
    /// For somebody with access to the server whom nobody else can help (the only administrator lost the phone and the recovery codes):
    /// enables the account, lifts a lockout, forgets two-factor and gives a new one-time password to change at the next sign-in.
    /// </summary>
    /// <param name="userNameOrEmail">Whichever the account has.</param>
    /// <returns>The user and the new one-time password, or null when there is no such user.</returns>
    Task<(UserSummary User, string TemporaryPassword)?> RecoverAccessAsync(string userNameOrEmail, CancellationToken cancellationToken);

    Task<UserChangeOutcome> SetDisabledAsync(Guid userId, bool disabled, Guid actingUserId, CancellationToken cancellationToken);

    /// <summary>Forgets the wrong passwords and codes counted against the user and lifts every lock they led to. Does not enable a disabled account.</summary>
    Task<UserChangeOutcome> UnlockAsync(Guid userId, CancellationToken cancellationToken);
}
