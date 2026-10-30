using Domain.Enums;

namespace BackgroundWorker.Health;

/// <summary>Rows newly stored since the last time the counters were taken.</summary>
public readonly record struct StoredCounts(long Candles, long FundingRates, long OpenInterest, long Liquidations, long MarketStatistics = 0);

/// <summary>
/// How much the collector has stored, for the periodic summary in the log. The collectors report after every batch they write:
/// a single interlocked add, so it is safe on the hot path. Only rows that were really new are counted (re-fetching a range
/// the table already has adds nothing).
/// </summary>
public sealed class CollectionStats
{
    private readonly long[] _stored = new long[Enum.GetValues<MarketDataTypeEnum>().Max(t => (int)t) + 1];

    public void Stored(MarketDataTypeEnum dataType, int rows)
    {
        if (rows > 0)
            Interlocked.Add(ref _stored[(int)dataType], rows);
    }

    /// <summary>What was stored since the previous call (or since the start), and starts counting again from zero.</summary>
    public StoredCounts TakeSinceLastCall() => new(
        Take(MarketDataTypeEnum.Candle),
        Take(MarketDataTypeEnum.FundingRate),
        Take(MarketDataTypeEnum.OpenInterest),
        Take(MarketDataTypeEnum.Liquidation),
        // Long/short ratio and taker volume are one switch for the admin, so one number in the summary.
        Take(MarketDataTypeEnum.LongShortRatio) + Take(MarketDataTypeEnum.TakerVolume));

    private long Take(MarketDataTypeEnum dataType) => Interlocked.Exchange(ref _stored[(int)dataType], 0);
}
