using System.Collections.Frozen;
using Domain.Enums;

namespace Domain.Services;

/// <summary>
/// What each exchange market can provide: which kinds of data, and for the data that is split by time frame, which
/// time frames. The one place that knows, so the admin API (reject an impossible choice), the UI (hide it) and the
/// workers (ignore it) cannot disagree.
/// <para>
/// A new exchange or market is one more entry in <see cref="Markets"/>; nothing else needs to learn about it.
/// </para>
/// </summary>
public static class MarketDataSupport
{
    /// <summary>What one kind of data needs from the caller. <c>null</c> time frames: the data has no time frame at all.</summary>
    private sealed record Rule(FrozenSet<TimeFrameEnum>? TimeFrames);

    private static readonly FrozenSet<TimeFrameEnum> EveryTimeFrame = Enum.GetValues<TimeFrameEnum>().ToFrozenSet();

    // Binance's futures statistics endpoints (open interest history, ...) only know these periods. The worker maps them onto
    // Binance's own enum in BinancePeriodIntervals; a test keeps the two lists equal.
    private static readonly FrozenSet<TimeFrameEnum> OpenInterestTimeFrames = FrozenSet.ToFrozenSet(
    [
        TimeFrameEnum._5M, TimeFrameEnum._15M, TimeFrameEnum._30M, TimeFrameEnum._1H, TimeFrameEnum._2H,
        TimeFrameEnum._4H, TimeFrameEnum._6H, TimeFrameEnum._12H, TimeFrameEnum._1D,
    ]);

    private static readonly FrozenDictionary<(ExchangeEnum Exchange, MarketTypeEnum Market), FrozenDictionary<MarketDataTypeEnum, Rule>> Markets =
        new Dictionary<(ExchangeEnum, MarketTypeEnum), FrozenDictionary<MarketDataTypeEnum, Rule>>
        {
            [(ExchangeEnum.Binance, MarketTypeEnum.Spot)] = new Dictionary<MarketDataTypeEnum, Rule>
            {
                [MarketDataTypeEnum.Candle] = new(EveryTimeFrame),
            }.ToFrozenDictionary(),

            [(ExchangeEnum.Binance, MarketTypeEnum.Futures)] = new Dictionary<MarketDataTypeEnum, Rule>
            {
                [MarketDataTypeEnum.Candle] = new(EveryTimeFrame),
                [MarketDataTypeEnum.FundingRate] = new((FrozenSet<TimeFrameEnum>?)null),
                [MarketDataTypeEnum.OpenInterest] = new(OpenInterestTimeFrames),
                [MarketDataTypeEnum.LongShortRatio] = new(OpenInterestTimeFrames),
                [MarketDataTypeEnum.TakerVolume] = new(OpenInterestTimeFrames),
                [MarketDataTypeEnum.Liquidation] = new((FrozenSet<TimeFrameEnum>?)null),
            }.ToFrozenDictionary(),
        }.ToFrozenDictionary();

    public static bool IsSupported(ExchangeEnum exchange, MarketTypeEnum market, MarketDataTypeEnum dataType, TimeFrameEnum? timeFrame = null) =>
        TryValidate(exchange, market, dataType, timeFrame, out _);

    /// <summary>
    /// True when the exchange market provides <paramref name="dataType"/> (for <paramref name="timeFrame"/>, when the data has
    /// one). Otherwise false, with a reason fit to show to an admin or to put in a log. A time frame given for data that has
    /// none (funding rate, liquidation) is ignored.
    /// </summary>
    public static bool TryValidate(ExchangeEnum exchange, MarketTypeEnum market, MarketDataTypeEnum dataType, TimeFrameEnum? timeFrame, out string reason)
    {
        if (!Enum.IsDefined(exchange))
            return Refuse("unknown exchange", out reason);
        if (!Enum.IsDefined(market))
            return Refuse("unknown market", out reason);
        if (!Enum.IsDefined(dataType))
            return Refuse("unknown data type", out reason);

        if (!Markets.TryGetValue((exchange, market), out var dataTypes) || !dataTypes.TryGetValue(dataType, out var rule))
            return Refuse($"{dataType} is not available on {exchange} {market}", out reason);

        if (rule.TimeFrames is null)
        {
            reason = string.Empty;
            return true;
        }

        if (timeFrame is null)
            return Refuse($"{dataType} needs a time frame", out reason);
        if (!Enum.IsDefined(timeFrame.Value))
            return Refuse("unknown time frame", out reason);
        if (!rule.TimeFrames.Contains(timeFrame.Value))
            return Refuse($"{dataType} on {exchange} {market} does not support the {timeFrame.Value.ToString().TrimStart('_')} time frame", out reason);

        reason = string.Empty;
        return true;
    }

    /// <summary>The time frames the data can be collected for, shortest first. Empty when the data has none or is not available.</summary>
    public static IReadOnlyList<TimeFrameEnum> GetTimeFrames(ExchangeEnum exchange, MarketTypeEnum market, MarketDataTypeEnum dataType) =>
        Markets.TryGetValue((exchange, market), out var dataTypes) && dataTypes.TryGetValue(dataType, out var rule) && rule.TimeFrames is not null
            ? [.. rule.TimeFrames.Order()]
            : [];

    private static bool Refuse(string why, out string reason)
    {
        reason = why;
        return false;
    }
}
