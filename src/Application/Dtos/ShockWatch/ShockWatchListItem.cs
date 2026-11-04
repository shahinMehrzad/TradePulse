using Domain.Entities;
using Domain.Enums;

namespace Application.Dtos.ShockWatch;

/// <summary>One symbol of the watch list, with the listing spelled out (ids and the exchange's own name).</summary>
public sealed class ShockWatchListItem
{
    /// <summary>The id of the row, or 0 for a symbol that is watched only because it is enabled for trading (there is no row yet).</summary>
    public int Id { get; set; }
    public int SymbolListingId { get; set; }
    public int ExchangeId { get; set; }
    public int MarketId { get; set; }
    public int SymbolId { get; set; }

    /// <summary>The name the exchange uses for the symbol, e.g. <c>BTCUSDT</c>.</summary>
    public string ExchangeSymbol { get; set; } = "";

    /// <summary>
    /// Why it is on the list: <c>Manual</c> (the admin added it to study it) or <c>Trading</c> (it is enabled for trading, so it is
    /// watched on its own; a row of this kind only exists when the admin switched its watch off).
    /// </summary>
    public string Reason { get; set; } = "";

    /// <summary>Whether the watch follows it right now (a row added by hand and switched off is still followed while the symbol is enabled for trading).</summary>
    public bool IsEnabled { get; set; }

    /// <summary>Whether the symbol is enabled for trading right now (whatever the reason it is on the list).</summary>
    public bool IsTradedNow { get; set; }

    /// <summary>
    /// Whether the symbol's 1 minute candles are being collected. The watch starts from them; without them it is silent for an hour
    /// after every start, and the panel should say so.
    /// </summary>
    public bool HasMinuteCandles { get; set; }

    /// <summary>UTC. Null for a symbol with no row.</summary>
    public DateTime? CreateDate { get; set; }

    /// <param name="configuration">With its <see cref="ShockWatchConfiguration.SymbolListing"/> loaded.</param>
    public static ShockWatchListItem From(ShockWatchConfiguration configuration, bool hasMinuteCandles, bool tradedNow) => new()
    {
        Id = configuration.Id,
        SymbolListingId = configuration.SymbolListingId,
        ExchangeId = configuration.SymbolListing!.ExchangeId,
        MarketId = configuration.SymbolListing.MarketId,
        SymbolId = configuration.SymbolListing.SymbolId,
        ExchangeSymbol = configuration.SymbolListing.ExchangeSymbol,
        Reason = configuration.Reason.ToString(),
        IsEnabled = Application.Research.Watch.ConfiguredWatchedSymbols.IsWatched(configuration, tradedNow),
        IsTradedNow = tradedNow,
        HasMinuteCandles = hasMinuteCandles,
        CreateDate = configuration.CreateDate,
    };

    /// <summary>A symbol enabled for trading that has no row: the watch follows it on its own.</summary>
    public static ShockWatchListItem FromTrading(SymbolListing listing, bool hasMinuteCandles) => new()
    {
        Id = 0,
        SymbolListingId = listing.Id,
        ExchangeId = listing.ExchangeId,
        MarketId = listing.MarketId,
        SymbolId = listing.SymbolId,
        ExchangeSymbol = listing.ExchangeSymbol,
        Reason = WatchReasonEnum.Trading.ToString(),
        IsEnabled = true,
        IsTradedNow = true,
        HasMinuteCandles = hasMinuteCandles,
    };
}
