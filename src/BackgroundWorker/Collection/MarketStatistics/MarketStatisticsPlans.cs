using BackgroundWorker.CandleSeries;
using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;

namespace BackgroundWorker.Collection.MarketStatistics;

public static class MarketStatisticsPlans
{
    /// <summary>
    /// The plans whose <paramref name="dataType"/> the Binance worker should collect: those the admin flagged "market statistics",
    /// on Binance, where the market really has it at that time frame. A flagged series that cannot have it (a row edited by hand
    /// in the database; the admin API refuses it) is reported through <paramref name="onRefused"/> and left out, so one bad row
    /// never stops the others, nor the candles of that very series.
    /// </summary>
    public static IReadOnlyList<CandleSeriesPlan> SelectForBinance(
        IEnumerable<CandleSeriesPlan> plans, MarketDataTypeEnum dataType, Action<CandleSeriesPlan, string>? onRefused = null)
    {
        var selected = new List<CandleSeriesPlan>();

        foreach (var plan in plans.ForExchange(ExchangeEnum.Binance).Where(p => p.CollectMarketStatistics).DistinctBy(p => p.Series))
        {
            var series = plan.Series;

            if (!MarketDataSupport.TryValidate(series.Exchange, series.MarketType, dataType, series.Timeframe, out var reason))
                onRefused?.Invoke(plan, reason);
            else
                selected.Add(plan);
        }

        return selected;
    }
}
