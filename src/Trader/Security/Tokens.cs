using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Trader.Security;

public sealed class JwtSettings
{
    public const string SectionName = "JwtConfigurations";

    /// <summary>The ES256 algorithm of the API's tokens. Never read from a token.</summary>
    public const string Algorithm = SecurityAlgorithms.EcdsaSha256;

    /// <summary>Base64 of the API's public key (SubjectPublicKeyInfo, ECDSA P-256). With it a token can be checked and not made.</summary>
    public string PublicKey { get; set; } = "";

    /// <summary>Must stay empty here: this program only believes tokens and must never be able to make one.</summary>
    public string PrivateKey { get; set; } = "";

    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";

    public SecurityKey VerificationKey { get; private set; } = null!;

    public void EnsureValid()
    {
        if (!string.IsNullOrWhiteSpace(PrivateKey))
            throw new InvalidOperationException(
                "JwtConfigurations:PrivateKey is set for the key vault. It only checks tokens and must not hold the signing key: " +
                "remove it from its environment and give it JwtConfigurations:PublicKey.");
        if (string.IsNullOrWhiteSpace(PublicKey))
            throw new InvalidOperationException(
                "JwtConfigurations:PublicKey must be set, the public half of the API's signing key " +
                "(environment variable JwtConfigurations__PublicKey; deploy/make-jwt-keys.sh makes the pair).");

        var key = ECDsa.Create();
        try
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey.Trim()), out _);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("JwtConfigurations:PublicKey is not a base64 SubjectPublicKeyInfo ECDSA public key.");
        }

        if (key.KeySize != 256)
            throw new InvalidOperationException($"JwtConfigurations:PublicKey must be an ECDSA key on the P-256 curve (ES256); this one is {key.KeySize} bits.");

        VerificationKey = new ECDsaSecurityKey(key);
    }
}

public static class TokenSecurity
{
    public const string AdminPolicy = "Admin";

    /// <summary>
    /// Believes the access tokens of the API (its public key, issuer and audience), so a request carries the user's own session and this
    /// program needs nothing else to know who is asking and that they are an administrator. It holds only the PUBLIC key: it can check a
    /// token and cannot make one, so a hacked vault cannot sign in as anybody on the API or the panel.
    /// </summary>
    public static IServiceCollection AddTokenSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();
        jwt.EnsureValid();
        services.AddSingleton(jwt);

        var adminRole = configuration["AdminConfigurations:Role"];
        if (string.IsNullOrWhiteSpace(adminRole))
            throw new InvalidOperationException("AdminConfigurations:Role must be set, the same as the API's.");

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = jwt.Issuer,
                ValidateAudience = true,
                ValidAudience = jwt.Audience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = jwt.VerificationKey,
                ValidAlgorithms = [JwtSettings.Algorithm],
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        });
        services.AddAuthorizationBuilder().AddPolicy(AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(adminRole));
        return services;
    }
}
