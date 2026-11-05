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
/// What the admin chose, and what follows from trading. Per symbol: <b>a row the admin switched on is watched</b>; <b>a symbol enabled for
/// trading is watched unless a row of kind <see cref="WatchReasonEnum.Trading"/> says off</b> (the admin switched the watch of a traded symbol
/// off on their own responsibility). A row added by hand and switched off only says "I am not studying it": it does not keep a symbol
/// that is enabled for trading from being watched (see <see cref="IsWatched"/>). An empty list is a valid answer: the watch then waits.
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
        var tradedNames = await traded.GetEnabledAsync(cancellationToken);
        var watched = rows.Where(c => IsWatched(c, IsTraded(c.SymbolListing!, tradedNames))).Select(c => Watched(c.SymbolListing!)).ToList();

        if (tradedNames.Count > 0)
        {
            var decided = rows.Select(c => c.SymbolListingId).ToHashSet();
            watched.AddRange(TradedListings(await catalog.GetListings(cancellationToken), tradedNames).Where(l => !decided.Contains(l.Id)).Select(Watched));
        }

        return watched;
    }

    /// <summary>The Binance Futures listings of the symbols enabled for trading (trading is on Futures only). A traded name nobody listed is left out: there is nothing to follow it by.</summary>
    internal static IEnumerable<SymbolListing> TradedListings(IEnumerable<SymbolListing> listings, IReadOnlySet<string> tradedNames) =>
        listings.Where(l => IsTraded(l, tradedNames));

    /// <summary>
    /// Whether the watch follows the symbol of a row. A switched-on row does. A row added by hand that is switched off does not either, unless
    /// the symbol is enabled for trading: enabling it for trading is the later decision, and every traded symbol is watched. Only a row of kind
    /// Trading that is switched off keeps a traded symbol unwatched, because it is what the admin does on their own responsibility.
    /// </summary>
    public static bool IsWatched(ShockWatchConfiguration row, bool tradedNow) =>
        row.IsEnabled || (row.Reason == WatchReasonEnum.Manual && tradedNow);

    /// <summary>True for the Binance Futures listing of a symbol enabled for trading.</summary>
    internal static bool IsTraded(SymbolListing listing, IReadOnlySet<string> tradedNames) =>
        listing.ExchangeId == (int)ExchangeEnum.Binance && listing.MarketId == (int)MarketTypeEnum.Futures
        && tradedNames.Contains(listing.ExchangeSymbol, StringComparer.OrdinalIgnoreCase);

    private static WatchedSymbol Watched(SymbolListing listing) =>
        new((ExchangeEnum)listing.ExchangeId, (MarketTypeEnum)listing.MarketId, listing.Id, new ExchangeSymbol(listing.ExchangeSymbol));
}
