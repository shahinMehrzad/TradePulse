using Domain.Entities;
using Domain.Repositories;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class RefreshTokenRepository(AuthDbContext context) : IRefreshTokenRepository
{
    public async Task AddAsync(RefreshToken refreshToken, CancellationToken cancellationToken = default)
    {
        await context.RefreshTokens.AddAsync(refreshToken, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Removes the tokens that ran out. A token that was used up (or revoked) but has not expired stays: it still says that the token existed and
    /// was spent, which is what a reuse of it has to be recognised by.
    /// </summary>
    public async Task CleanUpExpiredTokensAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await context.RefreshTokens.Where(r => r.ExpiresOn < now).ExecuteDeleteAsync(cancellationToken);
    }



    public async Task RevokeAllAsync(Guid userId, CancellationToken cancellationToken = default, string? exceptDeviceId = null) =>
        await context.RefreshTokens.Where(r => r.UserId == userId && !r.IsRevoked && (exceptDeviceId == null || r.DeviceId != exceptDeviceId))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRevoked, true), cancellationToken);

    /// <summary>A renewal that is repeated within this long (an answer that was lost and asked for again) is only refused, not taken for a theft.</summary>
    private static readonly TimeSpan ReuseGrace = TimeSpan.FromSeconds(10);

    public async Task RevokeDeviceAsync(Guid userId, string deviceId, CancellationToken cancellationToken = default) =>
        await context.RefreshTokens.Where(r => r.UserId == userId && r.DeviceId == deviceId && !r.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRevoked, true), cancellationToken);

    public async Task<RefreshTokenUse> ConsumeAsync(string tokenHash, string deviceId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var token = await context.RefreshTokens.AsNoTracking().Where(r => r.Token == tokenHash)
            .Select(r => new { r.UserId, r.DeviceId, r.IsRevoked, r.UsedAt, r.ExpiresOn })
            .FirstOrDefaultAsync(cancellationToken);
        if (token is null || token.ExpiresOn <= now)
            return RefreshTokenUse.Nothing;

        // Used up by a renewal and shown again: that is not a token that ran out or was ended, it is a copy of one that is still in use.
        if (token.UsedAt is { } usedAt)
            return now - usedAt > ReuseGrace ? new RefreshTokenUse(null, token.UserId, token.DeviceId) : RefreshTokenUse.Nothing;

        if (token.IsRevoked || token.DeviceId != deviceId)
            return RefreshTokenUse.Nothing;

        // One conditional UPDATE decides the race: only the call that flips IsRevoked from false gets the user.
        var claimed = await context.RefreshTokens
            .Where(r => r.Token == tokenHash && r.DeviceId == deviceId && !r.IsRevoked && r.ExpiresOn > now)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRevoked, true).SetProperty(r => r.UsedAt, now), cancellationToken);

        return claimed == 1 ? new RefreshTokenUse(token.UserId) : RefreshTokenUse.Nothing;
    }
}
