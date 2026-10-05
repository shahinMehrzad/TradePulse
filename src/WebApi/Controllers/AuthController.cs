using Application.Commands.Auth;
using Application.Dtos.Auth;
using Application.Interfaces;
using CryptoExchange.Net.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace WebApi.Controllers;

[Route("api/[controller]")]
[ApiController]
[AllowAnonymous]
public class AuthController(ICommandDispatcher dispatcher) : BaseController
{

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginCommand payload, CancellationToken cancellationToken)
    {
        try
        {
            var response = await dispatcher.SendAsync<LoginCommand, AuthResponseDto>(payload, cancellationToken);
            var refreshToken = await dispatcher.SendAsync<GenerateRefreshTokenCommand, string>
                (new GenerateRefreshTokenCommand(response.UserId, GetDeviceId(), GetIpAddress(), GetUserAgent(), response.RefreshTokenDurationInDays), cancellationToken);
            return Ok(new LoginResponseDto(response.AccessToken, response.ExpiresAt, response.UserName, refreshToken));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Unauthorized(new { message = ex.Message });
        }
    }

    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequestDto payload, CancellationToken cancellationToken)
    {
        Guid.TryParse(GetUserId(), out var userId);
        if (userId == default)
            return Unauthorized(new { message = "Invalid user ID." });

        try
        {
            var response = await dispatcher.SendAsync<ValidateRefreshTokenCommand, AuthResponseDto>
                (new ValidateRefreshTokenCommand(userId, payload.RefreshToken, GetDeviceId()), cancellationToken);
            var refreshToken = await dispatcher.SendAsync<GenerateRefreshTokenCommand, string>
                (new GenerateRefreshTokenCommand(userId, GetDeviceId(), GetIpAddress(), GetUserAgent(), response.RefreshTokenDurationInDays), cancellationToken);
            return Ok(new LoginResponseDto(response.AccessToken, response.ExpiresAt, response.UserName, refreshToken));
        }
        catch (Exception ex)
        {
            return Unauthorized(new { message = ex.Message });
        }        
    }
}
