using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace Infrastructure.Configurations;

/// <summary>
/// The keys behind <see cref="JwtConfigurations"/>, checked once so that a wrong one stops the host at start and not at the first sign-in.
/// <see cref="SigningKey"/> exists only where the private key is configured (the API). <see cref="VerificationKey"/> always exists.
/// The error messages name the setting and never contain any of the key.
/// </summary>
public sealed class JwtKeys
{
    /// <summary>The one algorithm tokens are signed and accepted with. Never read from the token, so a token cannot choose a weaker one.</summary>
    public const string Algorithm = SecurityAlgorithms.EcdsaSha256;

    private JwtKeys(SecurityKey? signingKey, SecurityKey verificationKey)
    {
        SigningKey = signingKey;
        VerificationKey = verificationKey;
    }

    public SecurityKey? SigningKey { get; }

    public SecurityKey VerificationKey { get; }

    /// <exception cref="InvalidOperationException">No key is set, or one is not a valid base64 ECDSA P-256 key, or the two do not belong together.</exception>
    public static JwtKeys From(JwtConfigurations jwt)
    {
        var hasPrivate = !string.IsNullOrWhiteSpace(jwt.PrivateKey);
        var hasPublic = !string.IsNullOrWhiteSpace(jwt.PublicKey);
        if (!hasPrivate && !hasPublic)
            throw new InvalidOperationException(
                "JwtConfigurations:PrivateKey (the API) or JwtConfigurations:PublicKey (a program that only checks tokens) must be set. " +
                "Make a pair with deploy/make-jwt-keys.sh and give it as environment variables, user secrets or a secret store.");

        ECDsa? signing = null;
        if (hasPrivate)
        {
            signing = ECDsa.Create();
            try
            {
                signing.ImportPkcs8PrivateKey(Convert.FromBase64String(jwt.PrivateKey.Trim()), out _);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                throw new InvalidOperationException("JwtConfigurations:PrivateKey is not a base64 PKCS#8 ECDSA private key (deploy/make-jwt-keys.sh makes one).");
            }

            RequireP256(signing, "JwtConfigurations:PrivateKey");
        }

        ECDsa verifying;
        if (hasPublic)
        {
            verifying = ECDsa.Create();
            try
            {
                verifying.ImportSubjectPublicKeyInfo(Convert.FromBase64String(jwt.PublicKey.Trim()), out _);
            }
            catch (Exception ex) when (ex is FormatException or CryptographicException)
            {
                throw new InvalidOperationException("JwtConfigurations:PublicKey is not a base64 SubjectPublicKeyInfo ECDSA public key (deploy/make-jwt-keys.sh makes one).");
            }

            RequireP256(verifying, "JwtConfigurations:PublicKey");

            // The two halves must be one pair, or the API would sign tokens that nobody else believes.
            if (signing is not null && !signing.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(verifying.ExportSubjectPublicKeyInfo()))
                throw new InvalidOperationException("JwtConfigurations:PublicKey is not the public half of JwtConfigurations:PrivateKey.");
        }
        else
        {
            verifying = signing!;
        }

        return new JwtKeys(signing is null ? null : new ECDsaSecurityKey(signing), new ECDsaSecurityKey(verifying));
    }

    private static void RequireP256(ECDsa key, string setting)
    {
        if (key.KeySize != 256)
            throw new InvalidOperationException($"{setting} must be an ECDSA key on the P-256 curve (ES256); this one is {key.KeySize} bits.");
    }
}
