using Domain.Enums;
using Infrastructure.Exchange.Ctrader.Protocol;

namespace Infrastructure.Exchange.Ctrader;

/// <summary>Which of cTrader's trendbar periods each of our time frames is, and how much time one request may ask for.</summary>
internal static class CtraderTimeFrames
{
    // cTrader has more periods (2, 4 and 10 minutes, weekly, monthly) and lacks some of ours (1s, 2H, 6H, 8H). A test keeps this list equal to
    // the time frames MarketDataSupport offers for Pepperstone.
    private static readonly Dictionary<TimeFrameEnum, ProtoOATrendbarPeriod> Periods = new()
    {
        [TimeFrameEnum._1M] = ProtoOATrendbarPeriod.M1,
        [TimeFrameEnum._3M] = ProtoOATrendbarPeriod.M3,
        [TimeFrameEnum._5M] = ProtoOATrendbarPeriod.M5,
        [TimeFrameEnum._15M] = ProtoOATrendbarPeriod.M15,
        [TimeFrameEnum._30M] = ProtoOATrendbarPeriod.M30,
        [TimeFrameEnum._1H] = ProtoOATrendbarPeriod.H1,
        [TimeFrameEnum._4H] = ProtoOATrendbarPeriod.H4,
        [TimeFrameEnum._12H] = ProtoOATrendbarPeriod.H12,
        [TimeFrameEnum._1D] = ProtoOATrendbarPeriod.D1,
    };

    public static IReadOnlyCollection<TimeFrameEnum> Supported => Periods.Keys;

    public static bool TryGetPeriod(TimeFrameEnum timeFrame, out ProtoOATrendbarPeriod period) => Periods.TryGetValue(timeFrame, out period);

    /// <summary>
    /// The longest stretch one candle request may cover, by cTrader's documentation (it grows with the period). Written from the documentation,
    /// not yet checked against the broker: a request that is too wide is refused by cTrader, which surfaces as a failed call, never as bad data.
    /// </summary>
    public static TimeSpan MaxRange(ProtoOATrendbarPeriod period) => period switch
    {
        ProtoOATrendbarPeriod.M1 or ProtoOATrendbarPeriod.M2 or ProtoOATrendbarPeriod.M3 or ProtoOATrendbarPeriod.M4 or ProtoOATrendbarPeriod.M5
            => TimeSpan.FromMilliseconds(302_400_000),
        ProtoOATrendbarPeriod.M10 or ProtoOATrendbarPeriod.M15 or ProtoOATrendbarPeriod.M30 or ProtoOATrendbarPeriod.H1
            => TimeSpan.FromMilliseconds(21_168_000_000),
        ProtoOATrendbarPeriod.H4 or ProtoOATrendbarPeriod.H12 or ProtoOATrendbarPeriod.D1
            => TimeSpan.FromMilliseconds(31_622_400_000),
        _ => TimeSpan.FromMilliseconds(158_112_000_000),
    };
}
