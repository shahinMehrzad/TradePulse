using Domain.Entities;
using Domain.Enums;
using Infrastructure.Configurations;

namespace BackgroundWorker.CandleSeries;

/// <summary>
/// Turns the rows the admin stored (ids) into the series the workers run on. The ids are the enum values, so the
/// mapping is a cast; a row whose id is not a member of an enum, or that is for another exchange, is skipped and
/// reported instead of crashing the collector or fetching something it cannot name.
/// </summary>
public static class CandleSeriesMapper
{
    public static IReadOnlyList<BinanceSymbolConfigurations> ToSeries(
        IEnumerable<CandleWorkerConfiguration> rows, Action<CandleWorkerConfiguration, string>? onSkipped = null)
    {
        var series = new List<BinanceSymbolConfigurations>();

        foreach (var row in rows)
        {
            if (row.ExchangeId != (int)ExchangeEnum.Binance)
                onSkipped?.Invoke(row, "only Binance is supported");
            else if (!Enum.IsDefined((MarketTypeEnum)row.MarketId))
                onSkipped?.Invoke(row, "unknown market");
            else if (!Enum.IsDefined((SymbolEnum)row.SymbolId))
                onSkipped?.Invoke(row, "unknown symbol");
            else if (!Enum.IsDefined((TimeFrameEnum)row.TimeFrameId))
                onSkipped?.Invoke(row, "unknown time frame");
            else
                series.Add(new BinanceSymbolConfigurations
                {
                    MarketType = (MarketTypeEnum)row.MarketId,
                    Name = (SymbolEnum)row.SymbolId,
                    Interval = (TimeFrameEnum)row.TimeFrameId,
                    // Stored as timestamp without a zone, always UTC.
                    StartDate = DateTime.SpecifyKind(row.StartDate, DateTimeKind.Utc),
                });
        }

        // The unique index already forbids this; the workers would double-fetch a repeated series, so be sure.
        return series.DistinctBy(s => (s.MarketType, s.Name, s.Interval)).ToList();
    }
}
