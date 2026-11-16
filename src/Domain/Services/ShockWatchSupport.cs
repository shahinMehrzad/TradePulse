using Domain.Entities;
using Domain.Enums;

namespace Domain.Services;

/// <summary>
/// What the price watch can follow. It judges the live trades of a symbol, and only Binance has a stream of them (spot and futures): a broker
/// (Pepperstone through cTrader) has prices and candles but no trades to watch. A symbol of any other exchange that gets onto the list would make the
/// watch ask a stream that does not know it, so it is refused when it is added and passed over when the list is read.
/// </summary>
public static class ShockWatchSupport
{
    public static bool IsSupported(ExchangeEnum exchange, MarketTypeEnum market) =>
        exchange == ExchangeEnum.Binance && market is MarketTypeEnum.Futures or MarketTypeEnum.Spot;

    /// <summary>False for a listing whose ids are not members of the enums (it cannot be followed either).</summary>
    public static bool IsSupported(SymbolListing listing) =>
        Enum.IsDefined((ExchangeEnum)listing.ExchangeId) && Enum.IsDefined((MarketTypeEnum)listing.MarketId)
        && IsSupported((ExchangeEnum)listing.ExchangeId, (MarketTypeEnum)listing.MarketId);

    /// <summary>The reason to give an admin who tries to put an unsupported listing on the list.</summary>
    public static string Refusal(SymbolListing listing) =>
        $"The price watch follows live trades, which only Binance has: {listing.ExchangeSymbol} on this exchange market cannot be watched.";
}
