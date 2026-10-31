using Domain.Entities;
using Domain.Exceptions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories;

/// <summary>
/// Temporary bridge until the admin chooses a listing itself: the API still receives exchange + market + symbol, and the
/// worker reads the listing, so the listing of that combination is found, or added when the symbol was never used on that
/// market, and the configuration points at it. Named like the exchange names it (BTC/USDT is BTCUSDT).
/// </summary>
internal static class SymbolListings
{
    public static async Task<SymbolListing> FindOrAddAsync(ConfigDbContext context, int exchangeId, int marketId, int symbolId, string unknownMessage, CancellationToken cancellationToken)
    {
        var listing = await context.SymbolListings.SingleOrDefaultAsync(
            l => l.ExchangeId == exchangeId && l.MarketId == marketId && l.SymbolId == symbolId, cancellationToken);
        if (listing is not null)
            return listing;

        var name = await context.Symbols.Where(s => s.Id == symbolId).Select(s => s.Name).SingleOrDefaultAsync(cancellationToken)
            ?? throw new ConfigurationValidationException(unknownMessage);

        listing = new SymbolListing { ExchangeId = exchangeId, MarketId = marketId, SymbolId = symbolId, ExchangeSymbol = name.Replace("/", "") };
        context.SymbolListings.Add(listing);
        return listing;
    }
}
