using Application.Interfaces;
using Domain.Repositories;

namespace Application.Commands.Auth;

/// <param name="DeviceId">The <c>X-Device-Id</c> of the session that signs out; empty when the caller sent none.</param>
public readonly record struct LogoutCommand(Guid UserId, string DeviceId);

/// <summary>
/// Signing out ends the session at the API too: the refresh token of that device stops working at once, instead of staying good for the days it
/// was made for. (The access token that was already issued still runs out by itself within minutes.) The other devices of the user stay signed in.
/// </summary>
public sealed class LogoutCommandHandler(IRefreshTokenRepository refreshTokens) : ICommandHandler<LogoutCommand, bool>
{
    public async Task<bool> HandleAsync(LogoutCommand request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(request.DeviceId))
            await refreshTokens.RevokeDeviceAsync(request.UserId, request.DeviceId, cancellationToken);

        return true;
    }
}
