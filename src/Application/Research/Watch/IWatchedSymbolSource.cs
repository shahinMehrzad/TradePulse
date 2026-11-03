using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Domain.ValueObjects;

namespace Application.Research.Watch;

/// <summary>Which symbols the watch follows right now. Asked again and again: the answer may change while the watch runs.</summary>
public interface IWatchedSymbolSource
{
    Task<IReadOnlyList<WatchedSymbol>> GetAsync(CancellationToken cancellationToken);
}

/// <summary>A list that never changes (the symbols given on the command line).</summary>
public sealed class FixedWatchedSymbols(IReadOnlyList<WatchedSymbol> symbols) : IWatchedSymbolSource
{
    public Task<IReadOnlyList<WatchedSymbol>> GetAsync(CancellationToken cancellationToken) => Task.FromResult(symbols);
}

/// <summary>
/// What the admin chose, and what follows from trading. Per symbol: <b>if the watch list has a row for it, the row decides</b> (the
/// admin added it by hand to study it, or switched the watch of a traded symbol off on their own responsibility); <b>with no row, a
/// symbol enabled for trading is watched</b>. An empty list is a valid answer: the watch then waits.
/// <para>
/// A failure to read either source is thrown, so the watch goes on with the list it has instead of dropping symbols over a hiccup.
/// </para>
/// </summary>
public sealed class ConfiguredWatchedSymbols(
    IShockWatchConfigurationRepository repository, ITradedSymbolSource traded, ISymbolCatalogRepository catalog) : IWatchedSymbolSource
{
    public async Task<IReadOnlyList<WatchedSymbol>> GetAsync(CancellationToken cancellationToken)
    {
        var rows = await repository.GetAll(cancellationToken);
        var watched = rows.Where(c => c.IsEnabled).Select(c => Watched(c.SymbolListing!)).ToList();

        var tradedNames = await traded.GetEnabledAsync(cancellationToken);
        if (tradedNames.Count > 0)
        {
            var decided = rows.Select(c => c.SymbolListingId).ToHashSet();
            watched.AddRange(TradedListings(await catalog.GetListings(cancellationToken), tradedNames).Where(l => !decided.Contains(l.Id)).Select(Watched));
        }

        return watched;
    }

    /// <summary>The Binance Futures listings of the symbols enabled for trading (trading is on Futures only). A traded name nobody listed is left out: there is nothing to follow it by.</summary>
    internal static IEnumerable<SymbolListing> TradedListings(IEnumerable<SymbolListing> listings, IReadOnlySet<string> tradedNames) =>
        listings.Where(l => l.ExchangeId == (int)ExchangeEnum.Binance && l.MarketId == (int)MarketTypeEnum.Futures
                            && tradedNames.Contains(l.ExchangeSymbol, StringComparer.OrdinalIgnoreCase));

    private static WatchedSymbol Watched(SymbolListing listing) =>
        new((ExchangeEnum)listing.ExchangeId, (MarketTypeEnum)listing.MarketId, listing.Id, new ExchangeSymbol(listing.ExchangeSymbol));
}
