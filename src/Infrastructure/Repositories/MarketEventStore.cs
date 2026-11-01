using Application.Research;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class MarketEventStore(ResearchDbContext context) : IMarketEventStore
{
    public async Task<int> AddNewAsync(IReadOnlyList<MarketEvent> events, CancellationToken cancellationToken)
    {
        var added = 0;

        // An event is the same as a stored one when listing, detector version and start are; the unique index says the same.
        foreach (var group in events.GroupBy(e => (e.SymbolListingId, e.DetectorVersion)))
        {
            var first = group.Min(e => e.StartedAt);
            var last = group.Max(e => e.StartedAt);

            var stored = (await context.MarketEvents
                    .Where(e => e.SymbolListingId == group.Key.SymbolListingId && e.DetectorVersion == group.Key.DetectorVersion
                                && e.StartedAt >= first && e.StartedAt <= last)
                    .Select(e => e.StartedAt)
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            var fresh = group.Where(e => !stored.Contains(e.StartedAt)).ToList();
            context.MarketEvents.AddRange(fresh);
            added += fresh.Count;
        }

        await context.SaveChangesAsync(cancellationToken);
        return added;
    }

    public async Task<IReadOnlyList<MarketEvent>> ListAsync(int symbolListingId, DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        var query = context.MarketEvents.AsNoTracking().Include(e => e.Reactions).Where(e => e.SymbolListingId == symbolListingId);

        if (from is { } start)
            query = query.Where(e => e.StartedAt >= start);
        if (to is { } end)
            query = query.Where(e => e.StartedAt <= end);

        return await query.OrderBy(e => e.StartedAt).ToListAsync(cancellationToken);
    }
}
