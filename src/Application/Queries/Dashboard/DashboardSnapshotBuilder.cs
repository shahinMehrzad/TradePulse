using Application.Dtos.Candles;
using Application.Dtos.Dashboard;
using Application.Interfaces;
using Domain.Exceptions;
using Domain.Extensions;
using Domain.ValueObjects;

namespace Application.Queries.Dashboard;

public sealed class DashboardSnapshotBuilder(ICandleReader candleReader)
{
    /// <summary>How many of the newest candles a card covers: its sparkline, change, high, low and volume all describe these.</summary>
    public const int SparklinePoints = 24;

    /// <summary>The most candles a caller may ask a snapshot to cover.</summary>
    public const int MaxPoints = 500;

    public static readonly TimeSpan LateGrace = TimeSpan.FromSeconds(60);

    /// <param name="points">How many of the newest candles to summarise.</param>
    public async Task<DashboardSnapshot> BuildAsync(CandleSeries series, DateTime now, int points, CancellationToken cancellationToken)
    {
        var timeframe = series.Timeframe.ToRouteName();
        var interval = series.Interval;

        IReadOnlyList<CandleResponse> candles;
        try
        {
            candles = await candleReader.GetLatestAsync(series, points, null, cancellationToken);
        }
        catch (CandleSeriesNotFoundException)
        {
            // The collector creates the table when it first knows it should collect it; before that there is nothing to show.
            candles = [];
        }

        if (candles.Count == 0)
            return new DashboardSnapshot(timeframe, DashboardStatus.Empty, null, null, null, null, null, null, null, null, null, null);

        var first = candles[0];
        var last = candles[^1];

        // The change is the rise of the line the card draws: its first point to its last, so the colour always agrees with the
        // picture. A single candle has no line yet, so it is measured from its own open.
        var from = candles.Count > 1 ? first.Close : first.Open;
        var change = from == 0 ? 0m : Math.Round((last.Close / from - 1m) * 100m, 2);

        var lag = now - (last.OpenTime + interval);
        var lagSeconds = (int)Math.Max(0, Math.Floor(lag.TotalSeconds));
        var status = lag > interval + LateGrace ? DashboardStatus.Late : DashboardStatus.Ok;

        return new DashboardSnapshot(
            timeframe, status, lagSeconds, last.OpenTime,
            last.Close, change, candles.Max(c => c.High), candles.Min(c => c.Low), candles.Sum(c => c.Volume),
            first.OpenTime, last.OpenTime + interval,
            [.. candles.Select(c => c.Close)]);
    }
}
