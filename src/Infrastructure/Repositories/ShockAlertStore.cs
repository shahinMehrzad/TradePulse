using Application.Research.Watch;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class ShockAlertStore(ResearchDbContext context) : IShockAlertStore
{
    public async Task SaveAsync(ShockAlert alert, CancellationToken cancellationToken)
    {
        // An alert is the same as a stored one when listing, rule version and start are; the unique index says the same. It is saved
        // when it starts and again when it ends, so the second save only brings the final level and the largest move.
        var updated = await context.ShockAlerts
            .Where(a => a.SymbolListingId == alert.SymbolListingId && a.RuleVersion == alert.RuleVersion && a.TriggeredAt == alert.TriggeredAt)
            .ExecuteUpdateAsync(set => set
                .SetProperty(a => a.Direction, alert.Direction)
                .SetProperty(a => a.Level, alert.Level)
                .SetProperty(a => a.WindowSeconds, alert.WindowSeconds)
                .SetProperty(a => a.Move, alert.Move)
                .SetProperty(a => a.ZScore, alert.ZScore)
                .SetProperty(a => a.StartPrice, alert.StartPrice)
                .SetProperty(a => a.EndPrice, alert.EndPrice)
                .SetProperty(a => a.Volume, alert.Volume)
                .SetProperty(a => a.FlaggedSeconds, alert.FlaggedSeconds), cancellationToken);

        if (updated > 0)
            return;

        context.ShockAlerts.Add(alert);
        await context.SaveChangesAsync(cancellationToken);

        // The watch lives for days with this one context: nothing may pile up in its tracker.
        context.ChangeTracker.Clear();
    }
}
