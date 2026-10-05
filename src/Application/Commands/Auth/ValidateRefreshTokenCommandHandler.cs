using Application.Dtos.Auth;
using Application.Interfaces;
using Domain.Repositories;
using Infrastructure.Configurations;
using Infrastructure.DbContext;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace Application.Commands.Auth;

public record ValidateRefreshTokenCommandHandler(IRefreshTokenRepository refreshTokenRepository,
    UserManager<ApplicationUser> userManager,
    IOptions<JwtConfigurations> jwtConfigs) : ICommandHandler<ValidateRefreshTokenCommand, AuthResponseDto>
{
    private readonly IRefreshTokenRepository _refreshTokenRepository = refreshTokenRepository;
    private readonly UserManager<ApplicationUser> _userManager = userManager;
    private readonly JwtConfigurations _jwtConfigs = jwtConfigs.Value;

    public async Task<AuthResponseDto> HandleAsync(ValidateRefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var user = await _userManager.FindByIdAsync(request.UserId.ToString());
        if (user == null)
            throw new UnauthorizedAccessException("Invalid token.");

        var revokeResponse =  await _refreshTokenRepository.RevokeTokenAsync(request.RefreshToken, request.UserId, request.DeviceId, cancellationToken);
        if (!revokeResponse)
            throw new UnauthorizedAccessException("Invalid token.");

        var tokenHandler = new JwtSecurityTokenHandler();
        var key = Encoding.UTF8.GetBytes(_jwtConfigs.Secret);
        var expireMinutes = _jwtConfigs.DurationInMinutes;
        var expiresAt = DateTime.UtcNow.AddMinutes(expireMinutes);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? string.Empty),
            new(ClaimTypes.Email, user.Email ?? string.Empty)
        };

        var roles = await _userManager.GetRolesAsync(user);
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt,
            Issuer = _jwtConfigs.Issuer,
            Audience = _jwtConfigs.Audience,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };

        var token = tokenHandler.CreateToken(tokenDescriptor);
        var accessToken = tokenHandler.WriteToken(token);

        return new AuthResponseDto(accessToken, expiresAt, user.UserName ?? string.Empty, user.Id, _jwtConfigs.RefreshTokenDurationInDays);
    }
}
