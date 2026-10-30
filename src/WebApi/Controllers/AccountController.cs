using Application.Commands.Auth;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace WebApi.Controllers;

/// <summary>
/// The signed-in user's own account. Any valid access token will do, not only an administrator's: this is what a future
/// ordinary user gets too. Everything here acts on the user in the token, never on an id from the request.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[Authorize]
public class AccountController(ICommandDispatcher dispatcher) : BaseController
{
    public record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public record RecoveryCodesRequest(string Password);
    public record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken) =>
        Guid.TryParse(GetUserId(), out var userId)
            ? Ok(await dispatcher.SendAsync<GetAccountQuery, AccountDto>(new GetAccountQuery(userId), cancellationToken))
            : Unauthorized();

    /// <summary>Other devices are signed out; the one sending <c>X-Device-Id</c> stays.</summary>
    [HttpPost("change-password")]
    [EnableRateLimiting(AuthController.LoginRateLimitPolicy)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest payload, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(GetUserId(), out var userId))
            return Unauthorized();

        await dispatcher.SendAsync<ChangeOwnPasswordCommand, bool>(
            new ChangeOwnPasswordCommand(userId, payload.CurrentPassword, payload.NewPassword, GetDeviceId()), cancellationToken);
        return NoContent();
    }

    /// <summary>New recovery codes, shown only in this answer. The old ones stop working.</summary>
    [HttpPost("recovery-codes")]
    [EnableRateLimiting(AuthController.LoginRateLimitPolicy)]
    public async Task<IActionResult> RecoveryCodes([FromBody] RecoveryCodesRequest payload, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(GetUserId(), out var userId))
            return Unauthorized();

        var codes = await dispatcher.SendAsync<RegenerateRecoveryCodesCommand, IReadOnlyList<string>>(
            new RegenerateRecoveryCodesCommand(userId, payload.Password), cancellationToken);
        return Ok(new RecoveryCodesResponse(codes));
    }
}
