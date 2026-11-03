using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Application.Interfaces;
using Infrastructure.Configurations;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Infrastructure.Identity;

public sealed class JwtAccessTokenService(IOptions<JwtConfigurations> options) : IAccessTokenService
{
    private readonly JwtConfigurations _jwt = options.Value;
    private readonly JwtKeys _keys = JwtKeys.From(options.Value);

    public AccessToken Create(AuthenticatedUser user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_jwt.DurationInMinutes);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.Email, user.Email)
        };
        claims.AddRange(user.Roles.Select(role => new Claim(ClaimTypes.Role, role)));

        var tokenHandler = new JwtSecurityTokenHandler();
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt,
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            SigningCredentials = new SigningCredentials(
                _keys.SigningKey ?? throw new InvalidOperationException("This program has no signing key (JwtConfigurations:PrivateKey): only the API makes tokens."),
                JwtKeys.Algorithm)
        };

        return new AccessToken(tokenHandler.WriteToken(tokenHandler.CreateToken(descriptor)), expiresAt, _jwt.RefreshTokenDurationInDays);
    }
}
