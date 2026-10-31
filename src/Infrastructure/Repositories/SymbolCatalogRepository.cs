using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public class SymbolCatalogRepository(ConfigDbContext context) : ISymbolCatalogRepository
{
    public async Task<IReadOnlyList<SymbolListing>> GetListings(CancellationToken cancellationToken)
        => await context.SymbolListings.AsNoTracking()
            .Include(l => l.Exchange).Include(l => l.Market).Include(l => l.Symbol)
            .OrderBy(l => l.ExchangeId).ThenBy(l => l.MarketId).ThenBy(l => l.Symbol!.Name)
            .ToListAsync(cancellationToken);

    public async Task<SymbolListing?> GetListing(int id, CancellationToken cancellationToken)
        => await context.SymbolListings.AsNoTracking().Include(l => l.Symbol).SingleOrDefaultAsync(l => l.Id == id, cancellationToken);

    public async Task<SymbolLookup?> GetSymbol(int id, CancellationToken cancellationToken)
        => await context.Symbols.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<(IReadOnlyList<SymbolLookup> Items, int Total)> GetSymbolsPage(string? keyword, int skip, int take, CancellationToken cancellationToken)
    {
        var query = context.Symbols.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var term = keyword.Trim().ToLower();
            query = query.Where(s =>
                s.Name.ToLower().Contains(term)
                || (s.Description != null && s.Description.ToLower().Contains(term))
                || context.SymbolListings.Any(l => l.SymbolId == s.Id && l.ExchangeSymbol.ToLower().Contains(term)));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderBy(s => s.Name).Skip(skip).Take(take).ToListAsync(cancellationToken);
        return (items, total);
    }

    public async Task<IReadOnlyDictionary<int, (int Series, int Settings)>> GetListingUsage(int symbolId, CancellationToken cancellationToken)
    {
        var listings = context.SymbolListings.Where(l => l.SymbolId == symbolId).Select(l => l.Id);

        var series = await context.CandleWorkerConfigurations.Where(c => listings.Contains(c.SymbolListingId))
            .GroupBy(c => c.SymbolListingId).Select(g => new { Listing = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);
        var settings = await context.SymbolDataConfigurations.Where(c => listings.Contains(c.SymbolListingId))
            .GroupBy(c => c.SymbolListingId).Select(g => new { Listing = g.Key, Count = g.Count() }).ToListAsync(cancellationToken);

        return series.Select(x => x.Listing).Union(settings.Select(x => x.Listing)).ToDictionary(
            id => id,
            id => (series.FirstOrDefault(x => x.Listing == id)?.Count ?? 0, settings.FirstOrDefault(x => x.Listing == id)?.Count ?? 0));
    }

    public async Task<SymbolLookup> UpdateSymbolDescription(int id, string? description, CancellationToken cancellationToken)
    {
        var entity = await context.Symbols.SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Symbol does not exist.");
        entity.Description = description;
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    public async Task<int> AddSymbol(SymbolLookup symbol, CancellationToken cancellationToken)
    {
        context.Symbols.Add(symbol);
        await SaveAsync(deleting: false, $"The symbol {symbol.Name} already exists.", "Unknown symbol.", cancellationToken);
        return symbol.Id;
    }

    public async Task RemoveSymbol(int id, CancellationToken cancellationToken)
    {
        var entity = await context.Symbols.SingleOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Symbol does not exist.");
        context.Symbols.Remove(entity);
        await SaveAsync(deleting: true, "The symbol is still listed on an exchange market.", "Unknown symbol.", cancellationToken);
    }

    public async Task<int> AddListing(SymbolListing listing, CancellationToken cancellationToken)
    {
        context.SymbolListings.Add(listing);
        await SaveAsync(deleting: false, "That symbol is already listed on this exchange market, or the exchange name is taken.", "Unknown exchange, market or symbol.", cancellationToken);
        return listing.Id;
    }

    public async Task RemoveListing(int id, CancellationToken cancellationToken)
    {
        var entity = await context.SymbolListings.SingleOrDefaultAsync(l => l.Id == id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Listing does not exist.");
        context.SymbolListings.Remove(entity);
        await SaveAsync(deleting: true, "A series or symbol-data setting still uses this listing.", "Unknown listing.", cancellationToken);
    }

    /// <summary>Saves, turning the two database errors a caller can cause into domain exceptions. Anything else is a real failure and is left to propagate.</summary>
    private async Task SaveAsync(bool deleting, string conflict, string unknown, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The unique index is the real guard; a check before saving can lose a race.
            throw new ConfigurationConflictException(conflict, ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // Deleting a row that something still points to is a conflict; saving a row that points to a missing one is a bad request.
            throw deleting ? new ConfigurationConflictException(conflict, ex) : new ConfigurationValidationException(unknown);
        }
    }
}
