using Domain.Entities;
using Domain.Repositories;
using Infrastructure.DbContext;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class RefreshTokenRepository(AuthDbContext context) : IRefreshTokenRepository
{
    public async Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default)
    {
        await context.RefreshTokens.AddAsync(refreshToken, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task CleanUpExpiredTokensAsync(CancellationToken cancellationToken = default)
    {
        var expiredTokens = await context.RefreshTokens
            .Where(r => r.ExpiresOn < DateTime.UtcNow || r.IsRevoked)
            .ToListAsync(cancellationToken);

        context.RefreshTokens.RemoveRange(expiredTokens);
        await context.SaveChangesAsync(cancellationToken);
    }



    public async Task<bool> RevokeTokenAsync(string token, Guid userId, string deviceId, CancellationToken cancellationToken = default)
    {
        var refreshToken = await GetByTokenAsync(token, userId, cancellationToken);

        if (refreshToken == null)
            return false;

        if(refreshToken.DeviceId != deviceId)
            return false;

        refreshToken.IsRevoked = true;
        await UpdateAsync(refreshToken, cancellationToken);
        return true;
    }

    private async Task<RefreshToken?> GetByTokenAsync(string token, Guid userId, CancellationToken cancellationToken = default)
    {        
        return await context.RefreshTokens
            .SingleOrDefaultAsync(r => r.Token == token && r.UserId == userId && !r.IsRevoked && r.ExpiresOn > DateTime.UtcNow, cancellationToken);
    }

    private async Task UpdateAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default)
    {
        context.RefreshTokens.Update(refreshToken);
        await context.SaveChangesAsync(cancellationToken);
    }
}
