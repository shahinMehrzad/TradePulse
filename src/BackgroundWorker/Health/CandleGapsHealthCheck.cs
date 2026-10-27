using System.Globalization;
using BackgroundWorker.CandleSeries;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BackgroundWorker.Health;

/// <summary>
/// Which candle tables have holes the collector could not fill. Always healthy: the holes are periods the exchange has no candles
/// for, which is a fact about the data and not a fault of the collector. The point is that whoever uses the data (the ML project)
/// can see where they are instead of finding out from a model. Only the series being collected now are listed.
/// </summary>
public sealed class CandleGapsHealthCheck(OpenGaps gaps, CandleSeriesProvider series) : IHealthCheck
{
    private const int MaxListed = 10;

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var tables = series.Current.Select(p => p.Series.TableName).Distinct().ToList();
        var looked = tables.Select(t => (Table: t, Summary: gaps.Get(t))).Where(x => x.Summary is not null).Select(x => (x.Table, Summary: x.Summary!.Value)).ToList();
        var open = looked.Where(x => x.Summary.Gaps > 0).OrderBy(x => x.Summary.Oldest).ThenBy(x => x.Table, StringComparer.Ordinal).ToList();
        var notLooked = tables.Count - looked.Count;

        var data = new Dictionary<string, object>();
        foreach (var (table, summary) in open.Take(MaxListed))
            data[table] = $"{summary.Gaps} gap(s), {summary.MissingCandles} candle(s) missing, from {summary.Oldest:yyyy-MM-dd HH:mm}";
        if (open.Count > MaxListed)
            data["more"] = $"{open.Count - MaxListed} more series";

        var listedLater = looked.Where(x => x.Summary.DataStartsAt is not null).OrderBy(x => x.Table, StringComparer.Ordinal).ToList();
        if (listedLater.Count > 0)
        {
            var shown = listedLater.Take(3).Select(x => $"{x.Table} from {x.Summary.DataStartsAt:yyyy-MM-dd}");
            data["listed-later"] = $"{listedLater.Count} series start after their start date: {string.Join(", ", shown)}{(listedLater.Count > 3 ? ", ..." : "")}";
        }

        var pending = notLooked > 0 ? $" ({notLooked.ToString(CultureInfo.InvariantCulture)} not checked yet)" : "";

        if (looked.Count == 0)
            return Task.FromResult(HealthCheckResult.Healthy("The gap check has not finished for any series yet", data));

        var description = open.Count == 0
            ? $"No open gaps in {looked.Count} series{pending}"
            : $"{open.Count} of {looked.Count} series have gaps the exchange has no candles for ({open.Sum(x => x.Summary.MissingCandles)} candles missing){pending}";

        return Task.FromResult(HealthCheckResult.Healthy(description, data));
    }
}
