using Application.Commands.Users;
using Application.Interfaces;
using Microsoft.AspNetCore.Mvc;
using WebApi.Authentication;

namespace WebApi.Controllers.Admin;

/// <summary>
/// Accounts of the panel. Every change needs a fresh second-factor confirmation (<see cref="RequireElevationAttribute"/>). Every administrator may manage the others; nobody may reset, disable or lock out their own account
/// here, and the last active administrator cannot be disabled.
/// </summary>
public class UsersController(ICommandDispatcher dispatcher) : AdminControllerBase
{
    public record CreateAdministratorRequest(string UserName, string Email);

    [HttpGet]
    public async Task<IReadOnlyList<UserSummary>> List(CancellationToken cancellationToken) =>
        await dispatcher.SendAsync<ListUsersQuery, IReadOnlyList<UserSummary>>(new ListUsersQuery(), cancellationToken);

    /// <summary>Creates an administrator. The response holds their one-time password, which is not shown again.</summary>
    [HttpPost]
    [RequireElevation]
    public async Task<TemporaryPasswordDto> Create([FromBody] CreateAdministratorRequest payload, CancellationToken cancellationToken) =>
        await dispatcher.SendAsync<CreateAdministratorCommand, TemporaryPasswordDto>(
            new CreateAdministratorCommand(payload.UserName, payload.Email), cancellationToken);

    [HttpPost("{id:guid}/reset-password")]
    [RequireElevation]
    public async Task<TemporaryPasswordDto> ResetPassword(Guid id, CancellationToken cancellationToken) =>
        await dispatcher.SendAsync<ResetUserPasswordCommand, TemporaryPasswordDto>(new ResetUserPasswordCommand(id, ActingUserId), cancellationToken);

    [HttpPost("{id:guid}/reset-two-factor")]
    [RequireElevation]
    public async Task<IActionResult> ResetTwoFactor(Guid id, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync<ResetUserTwoFactorCommand, bool>(new ResetUserTwoFactorCommand(id, ActingUserId), cancellationToken);
        return NoContent();
    }

    [HttpPost("{id:guid}/disable")]
    [RequireElevation]
    public Task<IActionResult> Disable(Guid id, CancellationToken cancellationToken) => SetDisabled(id, true, cancellationToken);

    [HttpPost("{id:guid}/enable")]
    [RequireElevation]
    public Task<IActionResult> Enable(Guid id, CancellationToken cancellationToken) => SetDisabled(id, false, cancellationToken);

    private async Task<IActionResult> SetDisabled(Guid id, bool disabled, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync<SetUserDisabledCommand, bool>(new SetUserDisabledCommand(id, disabled, ActingUserId), cancellationToken);
        return NoContent();
    }

    private Guid ActingUserId => Guid.TryParse(GetUserId(), out var id) ? id : Guid.Empty;
}
