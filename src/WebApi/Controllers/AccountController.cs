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
    public record EmailCodeRequest(string Code);
    /// <param name="Method">Authenticator (the default) or Email.</param>
    public record StepUpRequest(string Code, string? Method = null);

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

    /// <summary>Mails a test code to the user's address. Entering it (<c>email-codes/enable</c>) turns sign-in codes by email on.</summary>
    [HttpPost("email-codes/send")]
    [EnableRateLimiting(AuthController.LoginRateLimitPolicy)]
    public async Task<IActionResult> SendEmailCode(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(GetUserId(), out var userId))
            return Unauthorized();

        await dispatcher.SendAsync<SendOwnEmailCodeCommand, bool>(new SendOwnEmailCodeCommand(userId), cancellationToken);
        return NoContent();
    }

    [HttpPost("email-codes/enable")]
    [EnableRateLimiting(AuthController.LoginRateLimitPolicy)]
    public async Task<IActionResult> EnableEmailCodes([FromBody] EmailCodeRequest payload, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(GetUserId(), out var userId))
            return Unauthorized();

        await dispatcher.SendAsync<EnableEmailCodesCommand, bool>(new EnableEmailCodesCommand(userId, payload.Code), cancellationToken);
        return NoContent();
    }

    [HttpPost("email-codes/disable")]
    [EnableRateLimiting(AuthController.LoginRateLimitPolicy)]
    public async Task<IActionResult> DisableEmailCodes([FromBody] RecoveryCodesRequest payload, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(GetUserId(), out var userId))
            return Unauthorized();

        await dispatcher.SendAsync<DisableEmailCodesCommand, bool>(new DisableEmailCodesCommand(userId, payload.Password), cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Confirms with a second-factor code that it is really this user, and returns a short-lived elevation token. Send it in
    /// <c>X-Elevation</c> to the actions that need it. A recovery code does not count.
    /// </summary>
    [HttpPost("step-up")]
    [EnableRateLimiting(AuthController.LoginRateLimitPolicy)]
    public async Task<IActionResult> StepUp([FromBody] StepUpRequest payload, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(GetUserId(), out var userId))
            return Unauthorized();

        var method = Enum.TryParse<TwoFactorMethod>(payload.Method, ignoreCase: true, out var parsed) ? parsed : TwoFactorMethod.Authenticator;
        return Ok(await dispatcher.SendAsync<StepUpCommand, ElevationDto>(new StepUpCommand(userId, payload.Code, method), cancellationToken));
    }

    /// <summary>Mails a code for a step-up (for a user who turned email codes on).</summary>
    [HttpPost("step-up/email")]
    [EnableRateLimiting(AuthController.LoginRateLimitPolicy)]
    public async Task<IActionResult> StepUpEmailCode(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(GetUserId(), out var userId))
            return Unauthorized();

        await dispatcher.SendAsync<SendStepUpEmailCodeCommand, bool>(new SendStepUpEmailCodeCommand(userId), cancellationToken);
        return NoContent();
    }
}
