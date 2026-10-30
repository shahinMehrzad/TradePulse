using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Application.Interfaces;
using Infrastructure.Configurations;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Infrastructure.Identity;

/// <summary>
/// Signed with the same key as the access tokens but for another audience, so the API's bearer validation never accepts one
/// as an access token, and an access token is never accepted as a challenge.
/// </summary>
public sealed class JwtChallengeTokenService(IOptions<JwtConfigurations> options, TimeProvider time) : IChallengeTokenService
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private const string UserClaim = "uid";
    private readonly JwtConfigurations _jwt = options.Value;

    private string Audience => _jwt.Audience + ":login-challenge";

    private SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(_jwt.Secret));

    public ChallengeToken Create(Guid userId)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(Lifetime);
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(UserClaim, userId.ToString())]),
            NotBefore = now,
            Expires = expiresAt,
            Issuer = _jwt.Issuer,
            Audience = Audience,
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256Signature)
        };

        return new ChallengeToken(handler.WriteToken(handler.CreateToken(descriptor)), expiresAt);
    }

    public Guid? Validate(string token)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = Key,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(5),
            LifetimeValidator = (_, expires, _, _) => expires is { } e && e > time.GetUtcNow().UtcDateTime
        };

        try
        {
            var principal = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
            return Guid.TryParse(principal.FindFirstValue(UserClaim), out var userId) ? userId : null;
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }
}
