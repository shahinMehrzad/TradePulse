using Domain.Entities;

namespace Domain.Repositories;

public interface IRefreshTokenRepository
{    
    Task<bool> RevokeTokenAsync(string token, Guid userId, string deviceId, CancellationToken cancellationToken = default);
    Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default);
    Task CleanUpExpiredTokensAsync(CancellationToken cancellationToken = default);
}
