using Domain.Entities;

namespace Application.Dtos.ShockWatch;

/// <summary>One symbol of the watch list, with the listing spelled out (ids and the exchange's own name).</summary>
public sealed class ShockWatchListItem
{
    public int Id { get; set; }
    public int SymbolListingId { get; set; }
    public int ExchangeId { get; set; }
    public int MarketId { get; set; }
    public int SymbolId { get; set; }

    /// <summary>The name the exchange uses for the symbol, e.g. <c>BTCUSDT</c>.</summary>
    public string ExchangeSymbol { get; set; } = "";

    /// <summary>Why it is on the list: <c>Manual</c> (the admin added it) or <c>Trading</c> (it is enabled for trading).</summary>
    public string Reason { get; set; } = "";

    /// <summary>False when the admin switched the watch off for this symbol.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Whether the symbol's 1 minute candles are being collected. The watch starts from them; without them it is silent for an hour
    /// after every start, and the panel should say so.
    /// </summary>
    public bool HasMinuteCandles { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreateDate { get; set; }

    /// <param name="configuration">With its <see cref="ShockWatchConfiguration.SymbolListing"/> loaded.</param>
    public static ShockWatchListItem From(ShockWatchConfiguration configuration, bool hasMinuteCandles) => new()
    {
        Id = configuration.Id,
        SymbolListingId = configuration.SymbolListingId,
        ExchangeId = configuration.SymbolListing!.ExchangeId,
        MarketId = configuration.SymbolListing.MarketId,
        SymbolId = configuration.SymbolListing.SymbolId,
        ExchangeSymbol = configuration.SymbolListing.ExchangeSymbol,
        Reason = configuration.Reason.ToString(),
        IsEnabled = configuration.IsEnabled,
        HasMinuteCandles = hasMinuteCandles,
        CreateDate = configuration.CreateDate,
    };
}
