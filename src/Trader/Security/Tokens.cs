using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Trader.Security;

public sealed class JwtSettings
{
    public const string SectionName = "JwtConfigurations";

    private const int MinimumSecretBytes = 32;

    public string Secret { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Audience { get; set; } = "";

    public void EnsureStrong()
    {
        if (Encoding.UTF8.GetByteCount(Secret) < MinimumSecretBytes)
            throw new InvalidOperationException(
                $"JwtConfigurations:Secret must be set to at least {MinimumSecretBytes} bytes, the same as the API's " +
                "(environment variable JwtConfigurations__Secret or a secret store).");
    }
}

public static class TokenSecurity
{
    public const string AdminPolicy = "Admin";

    /// <summary>
    /// Believes the same access tokens as the API (same key, issuer and audience), so a request carries the user's own session and this
    /// program needs nothing else to know who is asking and that they are an administrator.
    /// </summary>
    public static IServiceCollection AddTokenSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        var jwt = configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();
        jwt.EnsureStrong();
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
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = TimeSpan.FromSeconds(30)
            };
        });
        services.AddAuthorizationBuilder().AddPolicy(AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(adminRole));
        return services;
    }
}

/// <summary>
/// Checks the elevation token the API hands out after a second-factor code (<c>POST api/account/step-up</c>): signed with the same key,
/// for the audience <c>{Audience}:elevation</c>. It only validates; the API is the one that issues them. The same few lines are in
/// <c>Infrastructure</c>, which this program does not reference; a test in <c>Trader.IntegrationTests</c> keeps the two in agreement.
/// </summary>
public sealed class ElevationValidator(JwtSettings jwt, TimeProvider time)
{
    public Guid? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = $"{jwt.Audience}:elevation",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Secret)),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(5),
            LifetimeValidator = (_, expires, _, _) => expires is { } e && e > time.GetUtcNow().UtcDateTime
        };

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
            return Guid.TryParse(principal.FindFirstValue("uid"), out var userId) ? userId : null;
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>The same rule as the API's <c>[RequireElevation]</c>: a fresh second-factor confirmation of this very user, or a 403 that says so.</summary>
public sealed class RequireElevationFilter(ElevationValidator validator) : IEndpointFilter
{
    public const string Header = "X-Elevation";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var elevated = validator.Validate(http.Request.Headers[Header].FirstOrDefault());

        if (elevated is { } userId && Guid.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var current) && userId == current)
            return await next(context);

        return Results.Problem(
            title: "Confirmation needed",
            detail: "Confirm with a code from your authenticator app (or email) to do this.",
            statusCode: StatusCodes.Status403Forbidden,
            extensions: new Dictionary<string, object?> { ["code"] = "step_up_required" });
    }
}
