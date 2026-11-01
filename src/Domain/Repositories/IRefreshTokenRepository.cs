using Domain.Entities;

namespace Domain.Repositories;

public interface IRefreshTokenRepository
{    
    /// <summary>
    /// Atomically uses up an active (not revoked, not expired) token issued to <paramref name="deviceId"/> and returns
    /// the user it belongs to, or null when there is no such token. Of two concurrent calls with the same token,
    /// exactly one gets the user. <paramref name="tokenHash"/> is the stored form, never the raw token.
    /// </summary>
    Task<Guid?> ConsumeAsync(string tokenHash, string deviceId, CancellationToken cancellationToken = default);
    Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default);
    /// <summary>Ends every session of the user: none of their refresh tokens works any more. <paramref name="exceptDeviceId"/> keeps one device signed in.</summary>
    Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default, string? exceptDeviceId = null);
    Task CleanUpExpiredTokensAsync(CancellationToken cancellationToken = default);
}
