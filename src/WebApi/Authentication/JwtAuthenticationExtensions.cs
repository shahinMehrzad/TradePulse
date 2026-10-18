using System.Text;
using Infrastructure.Configurations;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace WebApi.Authentication;

public static class JwtAuthenticationExtensions
{
    /// <summary>The key is used with HMAC-SHA256, which wants at least 256 bits.</summary>
    private const int MinimumSecretBytes = 32;

    /// <summary>Policy for the management endpoints: only the role seeded from <c>AdminConfigurations:Role</c>.</summary>
    public const string AdminPolicy = "Admin";

    /// <summary>
    /// Validates the bearer tokens that <c>POST /api/auth/login</c> issues: signature, issuer, audience and lifetime.
    /// The signing secret is deliberately not in the committed <c>appsettings.json</c> (this repo is public);
    /// outside Development it must come from the environment, e.g. <c>JwtConfigurations__Secret</c>.
    /// The host refuses to start without a strong one rather than run with tokens anyone could forge.
    /// </summary>
    public static IServiceCollection AddJwtAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(nameof(JwtConfigurations)).Get<JwtConfigurations>() ?? new JwtConfigurations();

        if (Encoding.UTF8.GetByteCount(jwt.Secret) < MinimumSecretBytes)
            throw new InvalidOperationException(
                $"JwtConfigurations:Secret must be set to at least {MinimumSecretBytes} bytes " +
                "(environment variable JwtConfigurations__Secret, user-secrets or a secret store).");

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
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256], // never trust the algorithm named in the token
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
