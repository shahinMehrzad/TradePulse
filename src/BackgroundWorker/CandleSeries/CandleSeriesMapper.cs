using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace BackgroundWorker.CandleSeries;

/// <summary>
/// Turns the rows the admin stored into the plans the workers run on. A row whose id is not a member of an enum is skipped
/// and reported instead of crashing the collector. Rows for any exchange are kept: each exchange's workers pick their own
/// with <see cref="ForExchange"/>.
/// </summary>
public static class CandleSeriesMapper
{
    public static IReadOnlyList<CandleSeriesPlan> ToSeries(
        IEnumerable<CandleWorkerConfiguration> rows, Action<CandleWorkerConfiguration, string>? onSkipped = null)
    {
        var plans = new List<CandleSeriesPlan>();

        foreach (var row in rows)
        {
            if (CandleSeriesPlan.TryCreate(row, out var plan, out var reason))
                plans.Add(plan);
            else
                onSkipped?.Invoke(row, reason);
        }

        // The unique index already forbids this; the workers would double-fetch a repeated series, so be sure.
        return plans.DistinctBy(p => p.Series).ToList();
    }

    /// <summary>The plans one exchange's workers are responsible for.</summary>
    public static IEnumerable<CandleSeriesPlan> ForExchange(this IEnumerable<CandleSeriesPlan> plans, ExchangeEnum exchange) =>
        plans.Where(p => p.Series.Exchange == exchange);
}
