using Binance.Net.Enums;
using Domain.Enums;

namespace BackgroundWorker;

/// <summary>
/// Maps our timeframes onto the "period" values accepted by Binance's futures statistics endpoints
/// (open interest history, long/short ratios). Not every timeframe exists there (no 1s, 1m, 3m, 8h),
/// so the mapping is explicit instead of a numeric cast.
/// </summary>
public static class BinancePeriodIntervals
{
    public static bool TryMap(TimeFrameEnum timeFrame, out PeriodInterval period)
    {
        switch (timeFrame)
        {
            case TimeFrameEnum._5M: period = PeriodInterval.FiveMinutes; return true;
            case TimeFrameEnum._15M: period = PeriodInterval.FifteenMinutes; return true;
            case TimeFrameEnum._30M: period = PeriodInterval.ThirtyMinutes; return true;
            case TimeFrameEnum._1H: period = PeriodInterval.OneHour; return true;
            case TimeFrameEnum._2H: period = PeriodInterval.TwoHour; return true;
            case TimeFrameEnum._4H: period = PeriodInterval.FourHour; return true;
            case TimeFrameEnum._6H: period = PeriodInterval.SixHour; return true;
            case TimeFrameEnum._12H: period = PeriodInterval.TwelveHour; return true;
            case TimeFrameEnum._1D: period = PeriodInterval.OneDay; return true;
            default: period = default; return false;
        }
    }
}
