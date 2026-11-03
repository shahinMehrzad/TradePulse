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

    public async Task<Guid?> ConsumeAsync(string tokenHash, string deviceId, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var active = context.RefreshTokens.Where(r =>
            r.Token == tokenHash && r.DeviceId == deviceId && !r.IsRevoked && r.ExpiresOn > now);

        var userId = await active.Select(r => (Guid?)r.UserId).SingleOrDefaultAsync(cancellationToken);
        if (userId is null)
            return null;

        // One conditional UPDATE decides the race: only the call that flips IsRevoked from false gets the user.
        var claimed = await active.ExecuteUpdateAsync(s => s.SetProperty(r => r.IsRevoked, true), cancellationToken);

        return claimed == 1 ? userId : null;
    }
}
