using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Infrastructure.Configurations;
using Microsoft.IdentityModel.Tokens;

namespace Infrastructure.Identity;

/// <summary>
/// A short-lived signed token that names a user and is good for one purpose only. It is signed with the key of the access tokens
/// but for an audience of its own, so the API's bearer validation never takes one for an access token, and one purpose's token is
/// never taken for another's.
/// </summary>
internal sealed class PurposeTokens(JwtConfigurations jwt, TimeProvider time, string purpose, TimeSpan lifetime)
{
    private const string UserClaim = "uid";

    private string Audience => $"{jwt.Audience}:{purpose}";

    private SymmetricSecurityKey Key => new(Encoding.UTF8.GetBytes(jwt.Secret));

    public (string Token, DateTime ExpiresAt) Create(Guid userId)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(lifetime);
        var handler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity([new Claim(UserClaim, userId.ToString())]),
            NotBefore = now,
            Expires = expiresAt,
            Issuer = jwt.Issuer,
            Audience = Audience,
            SigningCredentials = new SigningCredentials(Key, SecurityAlgorithms.HmacSha256Signature)
        };

        return (handler.WriteToken(handler.CreateToken(descriptor)), expiresAt);
    }

    public Guid? Validate(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
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
