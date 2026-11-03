using Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using static Infrastructure.Identity.UserTokenProtection;

namespace Infrastructure.Identity;

/// <summary>
/// Identity's EF store, except that the authenticator key is stored encrypted and the recovery codes are stored hashed (see
/// <see cref="UserTokenProtection"/>). <see cref="UserManager{TUser}"/> and the token providers see the same values as before.
/// </summary>
public sealed class ProtectedUserStore(
    AuthDbContext context,
    IDataProtectionProvider dataProtection,
    IPasswordHasher<ApplicationUser> hasher,
    IdentityErrorDescriber? describer = null)
    : UserStore<ApplicationUser, ApplicationRole, AuthDbContext, Guid>(context, describer)
{
    private readonly IDataProtector _protector = AuthenticatorKeyProtector(dataProtection);

    public override Task SetAuthenticatorKeyAsync(ApplicationUser user, string key, CancellationToken cancellationToken) =>
        SetTokenAsync(user, LoginProvider, AuthenticatorKeyName, ProtectKey(_protector, key), cancellationToken);

    public override async Task<string?> GetAuthenticatorKeyAsync(ApplicationUser user, CancellationToken cancellationToken) =>
        UnprotectKey(_protector, await GetTokenAsync(user, LoginProvider, AuthenticatorKeyName, cancellationToken));

    public override Task ReplaceCodesAsync(ApplicationUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken) =>
        SetTokenAsync(user, LoginProvider, RecoveryCodesName, HashCodes(hasher, user, recoveryCodes), cancellationToken);

    /// <summary>A code works once: the matching hash is removed. Two requests that use the same code at the same moment are
    /// stopped by the user's concurrency stamp when <see cref="UserManager{TUser}"/> saves.</summary>
    public override async Task<bool> RedeemCodeAsync(ApplicationUser user, string code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(code))
            return false;

        var hashes = HashesOf(await GetTokenAsync(user, LoginProvider, RecoveryCodesName, cancellationToken));
        var match = Array.FindIndex(hashes, hash => hasher.VerifyHashedPassword(user, hash, code) != PasswordVerificationResult.Failed);
        if (match < 0)
            return false;

        await SetTokenAsync(user, LoginProvider, RecoveryCodesName, JoinHashes(hashes.Where((_, i) => i != match)), cancellationToken);
        return true;
    }

    public override async Task<int> CountCodesAsync(ApplicationUser user, CancellationToken cancellationToken) =>
        HashesOf(await GetTokenAsync(user, LoginProvider, RecoveryCodesName, cancellationToken)).Length;
}
