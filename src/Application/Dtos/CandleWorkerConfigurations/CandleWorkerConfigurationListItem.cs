namespace Application.Dtos.CandleWorkerConfigurations;

/// <summary>One row of the admin's candle series list: names instead of lookup rows, so a filter or sort can use them directly.</summary>
public sealed class CandleWorkerConfigurationListItem
{
    public int Id { get; set; }
    public int ExchangeId { get; set; }
    public string Exchange { get; set; } = "";
    public int MarketId { get; set; }
    public string Market { get; set; } = "";
    public int SymbolId { get; set; }
    public string Symbol { get; set; } = "";
    public string? SymbolDescription { get; set; }
    public int TimeFrameId { get; set; }
    public string TimeFrame { get; set; } = "";
    public int IntervalSeconds { get; set; }

    /// <summary>UTC.</summary>
    public DateTime StartDate { get; set; }
    public bool CollectOpenInterest { get; set; }
    public bool CollectMarketStatistics { get; set; }

    /// <summary>UTC.</summary>
    public DateTime CreateDate { get; set; }
}
