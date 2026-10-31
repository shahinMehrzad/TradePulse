namespace Domain.Entities;

/// <summary>
/// A symbol as one exchange lists it in one market: "BTC/USDT" on Binance Futures. Configurations point to this row,
/// so a symbol that is not listed in a market cannot be chosen for it.
/// </summary>
public class SymbolListing
{
    public int Id { get; set; }
    public int ExchangeId { get; set; }
    public int MarketId { get; set; }
    public int SymbolId { get; set; }
    public ExchangeLookup? Exchange { get; set; }
    public MarketLookup? Market { get; set; }
    public SymbolLookup? Symbol { get; set; }

    /// <summary>The name the exchange itself uses, e.g. <c>BTCUSDT</c>. Sent to its API; the table name is derived from it.</summary>
    public required string ExchangeSymbol { get; set; }
}
