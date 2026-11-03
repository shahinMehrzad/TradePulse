using Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Identity;

/// <summary>
/// How the second-factor secrets are kept in <c>auth.user_tokens</c>. Identity on its own stores them as plain text. Here the
/// authenticator key is encrypted with data protection, whose keys sit on their own volume and not in the database. The recovery
/// codes are stored as password hashes. A dump or a backup of the database alone therefore cannot produce codes or a working
/// recovery code.
/// </summary>
internal static class UserTokenProtection
{
    // Identity's own names for the rows it keeps for itself.
    public const string LoginProvider = "[AspNetUserStore]";
    public const string AuthenticatorKeyName = "AuthenticatorKey";
    public const string RecoveryCodesName = "RecoveryCodes";

    // The prefixes say which form a stored value is in, so a value from before this change is never mistaken for a protected one.
    private const string EncryptedPrefix = "dp1:";
    private const string HashedPrefix = "h1:";
    private const string Purpose = "TradePulse.Identity.AuthenticatorKey.v1";

    public static IDataProtector AuthenticatorKeyProtector(IDataProtectionProvider provider) => provider.CreateProtector(Purpose);

    public static string ProtectKey(IDataProtector protector, string key) => EncryptedPrefix + protector.Protect(key);

    /// <summary>The key, or null when the stored value is not in the encrypted form (the startup conversion leaves none behind).</summary>
    /// <exception cref="System.Security.Cryptography.CryptographicException">The data protection keys that encrypted it are gone.</exception>
    public static string? UnprotectKey(IDataProtector protector, string? stored) =>
        stored is not null && stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal)
            ? protector.Unprotect(stored[EncryptedPrefix.Length..])
            : null;

    public static string HashCodes(IPasswordHasher<ApplicationUser> hasher, ApplicationUser user, IEnumerable<string> codes) =>
        JoinHashes(codes.Select(code => hasher.HashPassword(user, code)));

    /// <summary>The hashes of the codes not used yet. A value that is not in the hashed form counts as no codes.</summary>
    public static string[] HashesOf(string? stored) =>
        stored is not null && stored.StartsWith(HashedPrefix, StringComparison.Ordinal)
            ? stored[HashedPrefix.Length..].Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];

    // A password hash is base64, so it never contains the separator.
    public static string JoinHashes(IEnumerable<string> hashes)
    {
        var joined = string.Join(';', hashes);
        return joined.Length == 0 ? string.Empty : HashedPrefix + joined;
    }

    /// <summary>
    /// Encrypts the authenticator keys and hashes the recovery codes that are still stored as plain text. These rows were written
    /// before this change. Runs at every start and does nothing once every row is converted.
    /// </summary>
    /// <returns>How many rows were converted.</returns>
    public static async Task<int> ProtectStoredAsync(
        AuthDbContext context, IDataProtectionProvider dataProtection, IPasswordHasher<ApplicationUser> hasher, CancellationToken cancellationToken)
    {
        var protector = AuthenticatorKeyProtector(dataProtection);
        var rows = await context.UserTokens
            .Where(t => t.LoginProvider == LoginProvider && (t.Name == AuthenticatorKeyName || t.Name == RecoveryCodesName))
            .ToListAsync(cancellationToken);

        var converted = 0;
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.Value))
                continue;

            if (row.Name == AuthenticatorKeyName && !row.Value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
            {
                row.Value = ProtectKey(protector, row.Value);
                converted++;
            }
            else if (row.Name == RecoveryCodesName && !row.Value.StartsWith(HashedPrefix, StringComparison.Ordinal))
            {
                var user = await context.Users.FindAsync([row.UserId], cancellationToken) ?? new ApplicationUser { Id = row.UserId };
                row.Value = HashCodes(hasher, user, row.Value.Split(';', StringSplitOptions.RemoveEmptyEntries));
                converted++;
            }
        }

        if (converted > 0)
            await context.SaveChangesAsync(cancellationToken);
        return converted;
    }
}
