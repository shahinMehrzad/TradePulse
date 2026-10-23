using BackgroundWorker.CandleSeries;
using BackgroundWorker.Exchange.Binance;
using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;

namespace BackgroundWorker.Collection.OpenInterests;

public static class OpenInterestPlans
{
    /// <summary>
    /// The plans whose open interest the Binance worker should collect: those the admin flagged, on Binance, where the market
    /// really has open interest at that time frame. A flagged series that cannot have it (a row edited by hand in the
    /// database; the admin API refuses it) is reported through <paramref name="onRefused"/> and left out, so one bad row never
    /// stops the others, nor the candles of that very series.
    /// </summary>
    public static IReadOnlyList<CandleSeriesPlan> SelectForBinance(IEnumerable<CandleSeriesPlan> plans, Action<CandleSeriesPlan, string>? onRefused = null)
    {
        var selected = new List<CandleSeriesPlan>();

        foreach (var plan in plans.ForExchange(ExchangeEnum.Binance).Where(p => p.CollectOpenInterest).DistinctBy(p => p.Series))
        {
            var series = plan.Series;

            if (!MarketDataSupport.TryValidate(series.Exchange, series.MarketType, MarketDataTypeEnum.OpenInterest, series.Timeframe, out var reason))
                onRefused?.Invoke(plan, reason);
            // The rule above and Binance's own period list are kept equal by a test; this is the safety net if they ever drift.
            else if (!BinancePeriodIntervals.TryMap(series.Timeframe, out _))
                onRefused?.Invoke(plan, "Binance has no such open interest period");
            else
                selected.Add(plan);
        }

        return selected;
    }
}
