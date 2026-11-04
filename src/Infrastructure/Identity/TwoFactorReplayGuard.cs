using System.Security.Cryptography;
using System.Text;
using Application.Interfaces;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Identity;

/// <summary><see cref="ITwoFactorReplayGuard"/> on the auth schema.</summary>
public sealed class TwoFactorReplayGuard(AuthDbContext context, TimeProvider time) : ITwoFactorReplayGuard
{
    /// <summary>Identity accepts a code for about two and a half minutes; after this the same digits may come round again by chance and are fine.</summary>
    private static readonly TimeSpan Remembered = TimeSpan.FromMinutes(10);

    public async Task<bool> TryUseAsync(Guid userId, string code, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await context.UsedTwoFactorCodes.Where(c => c.UsedAt < now - Remembered).ExecuteDeleteAsync(cancellationToken);

        context.UsedTwoFactorCodes.Add(new UsedTwoFactorCode { UserId = userId, CodeHash = Hash(userId, code), UsedAt = now });
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // The unique index said no: this user used this code a moment ago (or at this very moment, in another request).
            context.ChangeTracker.Clear();
            return false;
        }
    }

    private static string Hash(Guid userId, string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userId:N}:{code}")));
}
