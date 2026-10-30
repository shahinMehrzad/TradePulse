using Application.Interfaces;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;

namespace Infrastructure.Identity;

public sealed class IdentityService(UserManager<ApplicationUser> userManager, SignInManager<ApplicationUser> signInManager) : IIdentityService
{
    private const string Issuer = "TradePulse";
    private const int RecoveryCodeCount = 10;

    public async Task<CredentialsResult> CheckCredentialsAsync(string email, string username, string password, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByEmailAsync(email) ?? await userManager.FindByNameAsync(username);
        if (user is null)
            return new CredentialsResult(CredentialsOutcome.Invalid);

        var result = await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        if (result.IsLockedOut)
            return new CredentialsResult(CredentialsOutcome.LockedOut);

        return result.Succeeded
            ? new CredentialsResult(CredentialsOutcome.Valid, await ToAccountAsync(user))
            : new CredentialsResult(CredentialsOutcome.Invalid);
    }

    public async Task<Account?> FindActiveAccountAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is null || user.IsDisabled() ? null : await ToAccountAsync(user);
    }

    public async Task<TwoFactorOutcome> VerifyTwoFactorAsync(Guid userId, string code, bool isRecoveryCode, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null || user.IsDisabled() || !user.TwoFactorEnabled)
            return TwoFactorOutcome.Invalid;
        if (await userManager.IsLockedOutAsync(user))
            return TwoFactorOutcome.LockedOut;

        var valid = isRecoveryCode
            ? (await userManager.RedeemTwoFactorRecoveryCodeAsync(user, code.Replace(" ", string.Empty))).Succeeded
            : await userManager.VerifyTwoFactorTokenAsync(user, userManager.Options.Tokens.AuthenticatorTokenProvider, Normalize(code));

        return await CountAsync(user, valid);
    }

    public async Task<PasswordChangeResult> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return new PasswordChangeResult(false, ["The user does not exist."]);

        var result = await userManager.ChangePasswordAsync(user, currentPassword, newPassword);
        if (!result.Succeeded)
            return new PasswordChangeResult(false, [.. result.Errors.Select(e => e.Description)]);

        user.MustChangePassword = false;
        var update = await userManager.UpdateAsync(user);
        return update.Succeeded
            ? new PasswordChangeResult(true, [])
            : new PasswordChangeResult(false, [.. update.Errors.Select(e => e.Description)]);
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
        if (user is null || user.IsDisabled() || user.TwoFactorEnabled || await userManager.IsLockedOutAsync(user))
            return null;

        var valid = await userManager.VerifyTwoFactorTokenAsync(user, userManager.Options.Tokens.AuthenticatorTokenProvider, Normalize(code));
        if (await CountAsync(user, valid) != TwoFactorOutcome.Valid)
            return null;

        await userManager.SetTwoFactorEnabledAsync(user, true);
        var codes = await userManager.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount);
        return [.. codes ?? []];
    }

    /// <summary>A wrong code counts towards the lockout; a right one clears the count.</summary>
    private async Task<TwoFactorOutcome> CountAsync(ApplicationUser user, bool valid)
    {
        if (valid)
        {
            await userManager.ResetAccessFailedCountAsync(user);
            return TwoFactorOutcome.Valid;
        }

        await userManager.AccessFailedAsync(user);
        return await userManager.IsLockedOutAsync(user) ? TwoFactorOutcome.LockedOut : TwoFactorOutcome.Invalid;
    }

    /// <summary>Apps show the code as <c>123 456</c>; people type it that way.</summary>
    private static string Normalize(string code) => code.Replace(" ", string.Empty).Replace("-", string.Empty);

    private async Task<Account> ToAccountAsync(ApplicationUser user) => new(
        new AuthenticatedUser(user.Id, user.UserName ?? string.Empty, user.Email ?? string.Empty, [.. await userManager.GetRolesAsync(user)]),
        user.MustChangePassword,
        user.TwoFactorEnabled);
}
