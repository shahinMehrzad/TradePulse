using Domain.Enums;
using Domain.Repositories;
using Domain.ValueObjects;

namespace Application.Signals;

/// <summary>
/// A symbol whose price the paper trades follow: its name in the signals (<c>BTC/USDT</c>) and the name the exchange itself uses
/// (<c>BTCUSDT</c>), on one exchange market.
/// </summary>
public sealed record FollowedSymbol(ExchangeEnum Exchange, MarketTypeEnum Market, string Symbol, ExchangeSymbol ExchangeSymbol)
{
    public (ExchangeEnum Exchange, string Symbol) Key => (Exchange, Symbol);

    /// <summary>
    /// For the symbol of an open trade that is no longer listed, so its name for the exchange is not known any more: the signal's name
    /// without the slash, which is how Binance names its USDT futures.
    /// </summary>
    public static FollowedSymbol Guess(ExchangeEnum exchange, string symbol) =>
        new(exchange, MarketTypeEnum.Futures, symbol, new ExchangeSymbol(symbol.Replace("/", "").ToUpperInvariant()));
}

/// <summary>The symbols whose price is followed all the time, so that a signal's entry price is always at hand.</summary>
public interface IFollowedSymbolSource
{
    Task<IReadOnlyList<FollowedSymbol>> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Every Futures symbol the admin has listed (the panel's Symbols page), whether or not its candles are collected. A new symbol, of a
/// strategy added next week or of another exchange, is followed by listing it. A signal for a symbol that is not listed is refused.
/// </summary>
public sealed class ListedSymbols(ISymbolCatalogRepository catalog) : IFollowedSymbolSource
{
    public async Task<IReadOnlyList<FollowedSymbol>> GetAsync(CancellationToken cancellationToken) =>
        (await catalog.GetListings(cancellationToken))
            .Where(l => l.MarketId == (int)MarketTypeEnum.Futures && Enum.IsDefined((ExchangeEnum)l.ExchangeId) && l.Symbol is not null)
            .Select(l => new FollowedSymbol((ExchangeEnum)l.ExchangeId, MarketTypeEnum.Futures, l.Symbol!.Name, new ExchangeSymbol(l.ExchangeSymbol)))
            .ToList();
}
