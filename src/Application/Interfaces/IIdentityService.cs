namespace Application.Interfaces;

public sealed record AuthenticatedUser(Guid Id, string UserName, string Email, IReadOnlyList<string> Roles);

/// <summary>A user who is signing in, and what still stands between them and a session.</summary>
public sealed record Account(AuthenticatedUser User, bool MustChangePassword, bool TwoFactorEnabled)
{
    /// <summary>True while the user has to change the password or enrol two-factor authentication before they get any token.</summary>
    public bool NeedsSetup => MustChangePassword || !TwoFactorEnabled;
}

public enum CredentialsOutcome { Valid, Invalid, LockedOut }

public sealed record CredentialsResult(CredentialsOutcome Outcome, Account? Account = null);

public enum TwoFactorOutcome { Valid, Invalid, LockedOut }

public sealed record TwoFactorEnrollment(string SharedKey, string AuthenticatorUri);

public sealed record PasswordChangeResult(bool Succeeded, IReadOnlyList<string> Errors);

public interface IIdentityService
{
    Task<CredentialsResult> CheckCredentialsAsync(string email, string username, string password, CancellationToken cancellationToken);

    /// <returns>The account, or null when there is no such user or the user is disabled.</returns>
    Task<Account?> FindActiveAccountAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Checks an authenticator code, or a recovery code (which is used up). Wrong codes count towards the lockout.</summary>
    Task<TwoFactorOutcome> VerifyTwoFactorAsync(Guid userId, string code, bool isRecoveryCode, CancellationToken cancellationToken);

    /// <summary>Replaces a password that has to be changed (a first password or one an admin reset). Clears the "must change" flag.</summary>
    Task<PasswordChangeResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken);

    /// <returns>The key to put in an authenticator app, or null when two-factor is already on.</returns>
    Task<TwoFactorEnrollment?> BeginTwoFactorEnrollmentAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Turns two-factor on when <paramref name="code"/> is what the app shows for the key from <see cref="BeginTwoFactorEnrollmentAsync"/>.</summary>
    /// <returns>The one-time recovery codes, or null when the code is wrong.</returns>
    Task<IReadOnlyList<string>?> ConfirmTwoFactorEnrollmentAsync(Guid userId, string code, CancellationToken cancellationToken);
}
