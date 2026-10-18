namespace Application.Queries.Candles;

public sealed class CandleCacheOptions
{
    public const string SectionName = "CandleCache";

    /// <summary>
    /// Requests that include the newest candle. A new candle closing is the only thing that changes such an answer,
    /// so this is also the longest a new candle can be missing from the API.
    /// </summary>
    public TimeSpan LatestTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Requests that end long before now, so every candle in them is closed. Bounded, not forever, because
    /// Binance sometimes revises historical candles.
    /// </summary>
    public TimeSpan HistoricalTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Keep entries in process memory (L1). Turn off only to measure or debug the shared distributed cache (L2) on its
    /// own: without L1 every hit costs a network round trip.
    /// </summary>
    public bool UseLocalCache { get; set; } = true;
}
