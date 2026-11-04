using Domain.Entities;

namespace Domain.Repositories;

/// <summary>What presenting a refresh token came to.</summary>
/// <param name="UserId">The user, when the token was good and is now used up.</param>
/// <param name="ReusedByUserId">The user, when the token had already been used up by a renewal and is shown again.</param>
/// <param name="ReusedDeviceId">The device that token was issued to, in that case.</param>
public sealed record RefreshTokenUse(Guid? UserId, Guid? ReusedByUserId = null, string? ReusedDeviceId = null)
{
    public static RefreshTokenUse Nothing { get; } = new(UserId: null);
}

public interface IRefreshTokenRepository
{    
    /// <summary>
    /// Atomically uses up an active (not revoked, not expired) token issued to <paramref name="deviceId"/> and returns
    /// the user it belongs to. Of two concurrent calls with the same token, exactly one gets the user. A token that a renewal used up
    /// more than a few seconds ago and is shown again is reported as reused (and gives nothing). <paramref name="tokenHash"/> is the
    /// stored form, never the raw token.
    /// </summary>
    Task<RefreshTokenUse> ConsumeAsync(string tokenHash, string deviceId, CancellationToken cancellationToken = default);
    Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default);
    /// <summary>Ends every session of the user: none of their refresh tokens works any more. <paramref name="exceptDeviceId"/> keeps one device signed in.</summary>
    Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default, string? exceptDeviceId = null);
    /// <summary>Ends the sessions of one device of the user (a sign-out); their other devices stay signed in.</summary>
    Task RevokeDeviceAsync(Guid userId, string deviceId, CancellationToken cancellationToken = default);
    Task CleanUpExpiredTokensAsync(CancellationToken cancellationToken = default);
}
