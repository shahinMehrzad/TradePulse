using Infrastructure.Configurations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace WebApi.Authentication;

public static class JwtAuthenticationExtensions
{
    /// <summary>Policy for the management endpoints: only the role seeded from <c>AdminConfigurations:Role</c>.</summary>
    public const string AdminPolicy = "Admin";

    /// <summary>
    /// Validates the bearer tokens that <c>POST /api/auth/login</c> issues: signature (ES256 only), issuer, audience and lifetime.
    /// The signing key is deliberately not in the committed <c>appsettings.json</c> (this repo is public);
    /// outside Development it must come from the environment, e.g. <c>JwtConfigurations__PrivateKey</c>.
    /// The host refuses to start without a valid one rather than run with tokens anyone could forge.
    /// </summary>
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(nameof(JwtConfigurations)).Get<JwtConfigurations>() ?? new JwtConfigurations();

        // The API is the one program that makes tokens: without the private key there is nothing to sign with.
        var keys = JwtKeys.From(jwt);
        if (keys.SigningKey is null)
            throw new InvalidOperationException(
                "JwtConfigurations:PrivateKey must be set (environment variable JwtConfigurations__PrivateKey, user-secrets or a secret store): " +
                "the API signs the tokens. Make a key pair with deploy/make-jwt-keys.sh.");

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = keys.VerificationKey,
                    ValidAlgorithms = [JwtKeys.Algorithm], // never trust the algorithm named in the token
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });

        var adminRole = configuration["AdminConfigurations:Role"];
        if (string.IsNullOrWhiteSpace(adminRole))
            throw new InvalidOperationException("AdminConfigurations:Role must be set; the admin policy has nothing to match without it.");

        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(adminRole));

        return services;
    }
}
