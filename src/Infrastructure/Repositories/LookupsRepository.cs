using Domain.Entities;
using Domain.Repositories;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

public class LookupsRepository(ConfigDbContext context) : ILookupsRepository
{
    public async Task<IEnumerable<ExchangeLookup>> GetExchangeListAsync(CancellationToken cancellationToken = default)
        => await context.Exchanges.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<IEnumerable<MarketLookup>> GetMarketListAsync(CancellationToken cancellationToken = default)
        => await context.Markets.AsNoTracking().ToListAsync(cancellationToken);

    public async Task<IEnumerable<SymbolLookup>> GetSymbolListAsync(string? keyword, CancellationToken cancellationToken = default)
    {
        var query = context.Symbols.AsQueryable();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLower();
            query = query.Where(x =>
                x.Name.ToLower().Contains(term) ||
                (x.Description != null && x.Description.ToLower().Contains(term)));
        }
        return await query.OrderBy(x => x.Name).AsNoTracking().ToListAsync(cancellationToken);
    }

    public async Task<IEnumerable<TimeFrameLookup>> GetTimeFrameListAsync(int minInterval = 0, int maxInterval = int.MaxValue, CancellationToken cancellationToken = default)
        => await context.TimeFrames.AsNoTracking()
            .Where(x => x.IntervalToSeconds >= minInterval && x.IntervalToSeconds <= maxInterval)
            .OrderBy(x => x.IntervalToSeconds)
            .ToListAsync(cancellationToken);
}
