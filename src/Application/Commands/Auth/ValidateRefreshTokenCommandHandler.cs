using Application.Dtos.Auth;
using Application.Interfaces;
using Application.Notifications;
using Application.Services;
using Domain.Entities;
using Domain.Repositories;

namespace Application.Commands.Auth;

public class ValidateRefreshTokenCommandHandler(
    IRefreshTokenRepository refreshTokenRepository,
    IIdentityService identityService,
    IAccessTokenService accessTokenService,
    AuditTrail audit,
    ISecurityAlerts alerts,
    IClientAddress client) : ICommandHandler<ValidateRefreshTokenCommand, AuthResponseDto>
{
    public async Task<AuthResponseDto> HandleAsync(ValidateRefreshTokenCommand request, CancellationToken cancellationToken)
    {
        // The refresh token alone identifies the user: the access token is usually expired by the time this is called.
        var use = await refreshTokenRepository.ConsumeAsync(RefreshTokenHasher.Hash(request.RefreshToken), request.DeviceId, cancellationToken);

        // A token that was already renewed, shown again: two holders of one session, and one of them is not the one it was given to.
        if (use.ReusedByUserId is { } victim)
        {
            await ReportReuseAsync(victim, use.ReusedDeviceId, cancellationToken);
            throw new UnauthorizedAccessException("Invalid token.");
        }

        if (use.UserId is null)
            throw new UnauthorizedAccessException("Invalid token.");

        // A disabled user, or one who has to set up again (password or two-factor reset), gets no new session from an old token.
        var account = await identityService.FindActiveAccountAsync(use.UserId.Value, cancellationToken);
        if (account is null || account.NeedsSetup)
            throw new UnauthorizedAccessException("Invalid token.");

        var token = accessTokenService.Create(account.User);
        return new AuthResponseDto(token.Token, token.ExpiresAt, account.User.UserName, account.User.Id, token.RefreshTokenDurationInDays);
    }

    /// <summary>Ends every session of that device (the thief's and the owner's: one of them has to sign in again, and it is the owner who knows), and says so.</summary>
    private async Task ReportReuseAsync(Guid userId, string? deviceId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(deviceId))
            await refreshTokenRepository.RevokeDeviceAsync(userId, deviceId, cancellationToken);

        var name = (await identityService.FindActiveAccountAsync(userId, cancellationToken))?.User.UserName ?? userId.ToString();
        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.User, 0, name,
            $"a refresh token that was already used was presented again from {client.Address}: that session was ended", cancellationToken, LoginGuard.AuditUserName);
        alerts.Raise(new SecurityAlert($"refresh-reuse:{userId}", $"Session copied for {name}",
            $"A refresh token that had already been used was presented again from {client.Address}. Somebody may hold a copy of the session; " +
            "it was ended, and the user has to sign in again. If you did not expect it, reset the password."));
    }
}
