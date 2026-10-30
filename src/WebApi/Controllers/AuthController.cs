using Application.Commands.Auth;
using Application.Dtos.Auth;
using Application.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace WebApi.Controllers;

/// <summary>
/// Signing in takes up to three calls: the password (<c>login</c>), then either the code of the second factor
/// (<c>login/two-factor</c>) or, for a user who has not been through it yet, the setup (<c>setup/password</c>,
/// <c>setup/two-factor/begin</c> and <c>setup/two-factor/confirm</c>). Only the last call of a path returns tokens.
/// </summary>
[Route("api/[controller]")]
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(LoginRateLimitPolicy)]
public class AuthController(ICommandDispatcher dispatcher) : BaseController
{
    public const string LoginRateLimitPolicy = "login";

    public record ChallengeRequest(string ChallengeToken);
    public record TwoFactorRequest(string ChallengeToken, string Code, bool IsRecoveryCode = false);
    public record SetupPasswordRequest(string ChallengeToken, string CurrentPassword, string NewPassword);
    public record ConfirmTwoFactorRequest(string ChallengeToken, string Code);

    [HttpPost("login")]
    public Task<IActionResult> Login([FromBody] LoginCommand payload, CancellationToken cancellationToken) =>
        Step(async () => await RespondAsync(await dispatcher.SendAsync<LoginCommand, LoginStepDto>(payload, cancellationToken), cancellationToken));

    [HttpPost("login/two-factor")]
    public Task<IActionResult> LoginTwoFactor([FromBody] TwoFactorRequest payload, CancellationToken cancellationToken) =>
        Step(async () => await RespondAsync(await dispatcher.SendAsync<VerifyTwoFactorCommand, LoginStepDto>(
            new VerifyTwoFactorCommand(payload.ChallengeToken, payload.Code, payload.IsRecoveryCode), cancellationToken), cancellationToken));

    [HttpPost("setup/password")]
    public Task<IActionResult> SetupPassword([FromBody] SetupPasswordRequest payload, CancellationToken cancellationToken) =>
        Step(async () => await RespondAsync(await dispatcher.SendAsync<ChangeInitialPasswordCommand, LoginStepDto>(
            new ChangeInitialPasswordCommand(payload.ChallengeToken, payload.CurrentPassword, payload.NewPassword), cancellationToken), cancellationToken));

    [HttpPost("setup/two-factor/begin")]
    public Task<IActionResult> BeginTwoFactor([FromBody] ChallengeRequest payload, CancellationToken cancellationToken) =>
        Step(async () => Ok(await dispatcher.SendAsync<BeginTwoFactorEnrollmentCommand, TwoFactorEnrollmentDto>(
            new BeginTwoFactorEnrollmentCommand(payload.ChallengeToken), cancellationToken)));

    [HttpPost("setup/two-factor/confirm")]
    public Task<IActionResult> ConfirmTwoFactor([FromBody] ConfirmTwoFactorRequest payload, CancellationToken cancellationToken) =>
        Step(async () =>
        {
            var enrolled = await dispatcher.SendAsync<ConfirmTwoFactorEnrollmentCommand, TwoFactorEnrolledDto>(
                new ConfirmTwoFactorEnrollmentCommand(payload.ChallengeToken, payload.Code), cancellationToken);
            return await RespondAsync(new LoginStepDto(LoginStage.Authenticated, enrolled.Session, null, false, false), cancellationToken, enrolled.RecoveryCodes);
        });

    [HttpPost("refresh-token")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequestDto payload, CancellationToken cancellationToken)
    {
        try
        {
            var response = await dispatcher.SendAsync<ValidateRefreshTokenCommand, AuthResponseDto>
                (new ValidateRefreshTokenCommand(payload.RefreshToken, GetDeviceId()), cancellationToken);
            return await RespondAsync(new LoginStepDto(LoginStage.Authenticated, response, null, false, false), cancellationToken);
        }
        catch (UnauthorizedAccessException)
        {
            // Deliberately generic, and only for authentication failures: other exceptions must not leak their messages.
            return Unauthorized(new { message = "Invalid token." });
        }
    }

    /// <summary>Runs one step; a refusal to authenticate is a 401 with the reason the handler gave (never a stack trace).</summary>
    private static async Task<IActionResult> Step(Func<Task<IActionResult>> step)
    {
        try
        {
            return await step();
        }
        catch (UnauthorizedAccessException ex)
        {
            return new UnauthorizedObjectResult(new { message = ex.Message });
        }
    }

    private async Task<IActionResult> RespondAsync(LoginStepDto step, CancellationToken cancellationToken, IReadOnlyList<string>? recoveryCodes = null)
    {
        string? refreshToken = null;
        if (step.Session is { } session)
            refreshToken = await dispatcher.SendAsync<GenerateRefreshTokenCommand, string>(
                new GenerateRefreshTokenCommand(session.UserId, GetDeviceId(), GetIpAddress(), GetUserAgent(), session.RefreshTokenDurationInDays), cancellationToken);

        return Ok(new LoginResponseDto(
            step.Stage.ToString(), step.Session?.AccessToken, step.Session?.ExpiresAt, step.Session?.UserName,
            refreshToken, step.ChallengeToken, step.MustChangePassword, step.NeedsTwoFactorSetup, recoveryCodes));
    }
}
