namespace Domain.Entities;

/// <summary>
/// Which of the data that has no time frame (funding rate, liquidations) the admin wants collected for one symbol of one
/// exchange market. One row per exchange + market + symbol; candles and open interest are chosen per time frame in
/// <see cref="CandleWorkerConfiguration"/> instead.
/// </summary>
public class SymbolDataConfiguration
{
    public int Id { get; set; }
    public int ExchangeId { get; set; }
    public int MarketId { get; set; }
    public int SymbolId { get; set; }
    public ExchangeLookup? Exchange { get; set; }
    public MarketLookup? Market { get; set; }
    public SymbolLookup? Symbol { get; set; }

    /// <summary>Transitional: filled by the migration, replaces <see cref="ExchangeId"/>, <see cref="MarketId"/> and <see cref="SymbolId"/> once the code uses it.</summary>
    public int? SymbolListingId { get; set; }
    public SymbolListing? SymbolListing { get; set; }

    public bool CollectFundingRate { get; set; }

    /// <summary>UTC. Where the funding rate history starts; set exactly when <see cref="CollectFundingRate"/> is on. Stored as <c>timestamp</c> (no zone).</summary>
    public DateTime? FundingStartDate { get; set; }

    /// <summary>Liquidations have no history: they are only recorded from the moment the collector runs.</summary>
    public bool CollectLiquidation { get; set; }

    public DateTime CreateDate { get; set; }
}
