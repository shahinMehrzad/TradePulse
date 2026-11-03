using Application.Interfaces;
using Application.Services;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace Infrastructure.Identity;

/// <summary>
/// Identity does the work of users, hashes and tokens; the counting of wrong guesses is not Identity's (its lockout is one count and one fixed
/// time for the whole account, which lets anybody who knows a user name keep that user out): <see cref="LoginGuard"/> does it, per address for
/// passwords and per account for codes, with locks that grow. "Locked out" of Identity is left for what it is used for here: a disabled account.
/// </summary>
public sealed class IdentityService(UserManager<ApplicationUser> userManager, LoginGuard guard) : IIdentityService
{
    private enum PasswordCheck { Ok, Wrong, Locked }

    private const string Issuer = "TradePulse";
    private const int RecoveryCodeCount = 10;

    public async Task<CredentialsResult> CheckCredentialsAsync(string email, string username, string password, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(email) ?? await userManager.FindByNameAsync(username);
        if (user is null)
            return new CredentialsResult(CredentialsOutcome.Invalid);

        return await CheckPasswordAsync(user, password, cancellationToken) switch
        {
            PasswordCheck.Locked => new CredentialsResult(CredentialsOutcome.LockedOut),
            PasswordCheck.Ok => new CredentialsResult(CredentialsOutcome.Valid, await ToAccountAsync(user)),
            _ => new CredentialsResult(CredentialsOutcome.Invalid)
        };
    }

    public async Task<Account?> FindActiveAccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is null || user.IsDisabled() ? null : await ToAccountAsync(user);
    }

    public async Task<TwoFactorOutcome> VerifyTwoFactorAsync(Guid userId, string code, TwoFactorMethod method, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDisabled() || !user.TwoFactorEnabled)
            return TwoFactorOutcome.Invalid;
        if (await TwoFactorClosedAsync(user, cancellationToken))
            return TwoFactorOutcome.LockedOut;

        if (method == TwoFactorMethod.Email && !user.EmailCodesEnabled)
            return TwoFactorOutcome.Invalid;

        var valid = method switch
        {
            TwoFactorMethod.Recovery => (await userManager.RedeemTwoFactorRecoveryCodeAsync(user, code.Replace(" ", string.Empty))).Succeeded,
            TwoFactorMethod.Email => await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider, Normalize(code)),
            _ => await userManager.VerifyTwoFactorTokenAsync(user, userManager.Options.Tokens.AuthenticatorTokenProvider, Normalize(code))
        };

        return await CountAsync(user, valid, cancellationToken);
    }

    public async Task<PasswordChangeResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return new PasswordChangeResult(false, ["The user does not exist."]);

        // A wrong current password is counted like one at sign-in: a stolen access token must not be a free password oracle.
        var check = await CheckPasswordAsync(user, currentPassword, cancellationToken);
        if (check != PasswordCheck.Ok)
            return new PasswordChangeResult(false, [check == PasswordCheck.Locked ? "Too many wrong passwords. Try again later." : "The current password is incorrect."]);

        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
            return new PasswordChangeResult(false, [.. result.Errors.Select(e => e.Description)]);

        user.MustChangePassword = false;
        var update = await userManager.UpdateAsync(user);
        return update.Succeeded
            ? new PasswordChangeResult(true, [])
            : new PasswordChangeResult(false, [.. update.Errors.Select(e => e.Description)]);
    }

    public async Task<EmailCode?> CreateEmailCodeAsync(Guid userId, bool forEnabling, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDisabled() || string.IsNullOrWhiteSpace(user.Email) || !user.TwoFactorEnabled)
            return null;
        if (!forEnabling && !user.EmailCodesEnabled)
            return null;

        var code = await userManager.GenerateTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider);
        return new EmailCode(user.Email, code);
    }

    public async Task<bool> EnableEmailCodesAsync(Guid userId, string code, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDisabled() || !user.TwoFactorEnabled || await TwoFactorClosedAsync(user, cancellationToken))
            return false;

        var valid = await userManager.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultEmailProvider, Normalize(code));
        if (await CountAsync(user, valid, cancellationToken) != TwoFactorOutcome.Valid)
            return false;

        // The code reached the mailbox: the address is proven, and the codes may be used to sign in.
        user.EmailConfirmed = true;
        user.EmailCodesEnabled = true;
        return (await userManager.UpdateAsync(user)).Succeeded;
    }

    public async Task<bool> DisableEmailCodesAsync(Guid userId, string password, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDisabled())
            return false;
        if (await CheckPasswordAsync(user, password, cancellationToken) != PasswordCheck.Ok)
            return false;

        user.EmailCodesEnabled = false;
        return (await userManager.UpdateAsync(user)).Succeeded;
    }

    public async Task<int> CountRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is null ? 0 : await userManager.CountRecoveryCodesAsync(user);
    }

    public async Task<IReadOnlyList<string>?> RegenerateRecoveryCodesAsync(Guid userId, string password, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDisabled() || !user.TwoFactorEnabled)
            return null;
        if (await CheckPasswordAsync(user, password, cancellationToken) != PasswordCheck.Ok)
            return null;

        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        return [.. codes ?? []];
    }

    public async Task<TwoFactorEnrollment?> BeginTwoFactorEnrollmentAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.TwoFactorEnabled)
            return null;

        // The key stays the same until two-factor is on, so a reloaded page shows the key the app may already hold.
        var key = await userManager.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await userManager.ResetAuthenticatorKeyAsync(user);
            key = await userManager.GetAuthenticatorKeyAsync(user);
        }

        var uri = $"otpauth://totp/{Issuer}:{Uri.EscapeDataString(user.UserName ?? string.Empty)}?secret={key}&issuer={Issuer}&digits=6";
        return new TwoFactorEnrollment(key!, uri);
    }

    public async Task<IReadOnlyList<string>?> ConfirmTwoFactorEnrollmentAsync(Guid userId, string code, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDisabled() || user.TwoFactorEnabled || await TwoFactorClosedAsync(user, cancellationToken))
            return null;

        var valid = await userManager.VerifyTwoFactorTokenAsync(user, userManager.Options.Tokens.AuthenticatorTokenProvider, Normalize(code));
        if (await CountAsync(user, valid, cancellationToken) != TwoFactorOutcome.Valid)
            return null;

        await userManager.SetTwoFactorEnabledAsync(user, true);
        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        return [.. codes ?? []];
    }

    /// <summary>A wrong code is counted against the account (and locks it for longer each round); a right one clears the count.</summary>
    private async Task<TwoFactorOutcome> CountAsync(ApplicationUser user, bool valid, CancellationToken cancellationToken)
    {
        if (valid)
        {
            await guard.TwoFactorSucceededAsync(user.Id, cancellationToken);
            return TwoFactorOutcome.Valid;
        }

        return await guard.TwoFactorFailedAsync(user.Id, user.UserName ?? string.Empty, cancellationToken)
            ? TwoFactorOutcome.LockedOut
            : TwoFactorOutcome.Invalid;
    }

    /// <summary>No code is taken while the account is disabled or locked for wrong codes.</summary>
    private async Task<bool> TwoFactorClosedAsync(ApplicationUser user, CancellationToken cancellationToken) =>
        await userManager.IsLockedOutAsync(user) || await guard.TwoFactorLockedAsync(user.Id, cancellationToken);

    /// <summary>
    /// A password, with the wrong ones counted. A disabled account (Identity's lockout) and an address that is locked out of the account are not
    /// even looked at: the password is not compared, so a locked address cannot go on guessing and learn nothing from the time it takes.
    /// </summary>
    private async Task<PasswordCheck> CheckPasswordAsync(ApplicationUser user, string password, CancellationToken cancellationToken)
    {
        if (await userManager.IsLockedOutAsync(user) || await guard.PasswordLockedAsync(user.Id, cancellationToken))
            return PasswordCheck.Locked;

        if (await userManager.CheckPasswordAsync(user, password))
        {
            await guard.PasswordSucceededAsync(user.Id, cancellationToken);
            return PasswordCheck.Ok;
        }

        await guard.PasswordFailedAsync(user.Id, user.UserName ?? string.Empty, cancellationToken);
        return PasswordCheck.Wrong;
    }

    /// <summary>Apps show the code as <c>123 456</c>; people type it that way.</summary>
    private static string Normalize(string code) => code.Replace(" ", string.Empty).Replace("-", string.Empty);

    private async Task<Account> ToAccountAsync(ApplicationUser user) => new(
        new AuthenticatedUser(user.Id, user.UserName ?? string.Empty, user.Email ?? string.Empty, [.. await userManager.GetRolesAsync(user)]),
        user.MustChangePassword,
        user.TwoFactorEnabled,
        user.EmailCodesEnabled);
}
