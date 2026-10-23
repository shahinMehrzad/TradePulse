using Application.Dtos.Candles;
using Application.Dtos.Dashboard;
using Application.Interfaces;
using Application.Queries.Candles;
using Domain.Exceptions;
using Domain.Extensions;
using Domain.ValueObjects;

namespace Application.Queries.Dashboard;

public sealed class DashboardSnapshotBuilder(ICandleReader candleReader)
{
    public const int SparklinePoints = 24;
    public static readonly TimeSpan LateGrace = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public async Task<DashboardSnapshot> BuildAsync(CandleSeries series, DateTime now, CancellationToken cancellationToken)
    {
        var timeframe = series.Timeframe.ToRouteName();
        var interval = series.Interval;

        var windowCandles = (int)Math.Clamp(Window / interval, 1, CandleReader.MaxLimit - 1);
        var take = Math.Max(windowCandles + 1, SparklinePoints);

        IReadOnlyList<CandleResponse> candles;
        try
        {
            candles = await candleReader.GetLatestAsync(series, take, null, cancellationToken);
        }
        catch (CandleSeriesNotFoundException)
        {
            candles = [];
        }

        if (candles.Count == 0)
            return new DashboardSnapshot(timeframe, DashboardStatus.Empty, null, null, null, null, null, null, null, null);

        var last = candles[^1];
        var inWindow = candles.Skip(Math.Max(0, candles.Count - windowCandles)).ToArray();

        var baseline = candles.Count > windowCandles ? candles[^(windowCandles + 1)].Close : candles[0].Open;
        var change = baseline == 0 ? 0m : Math.Round((last.Close / baseline - 1m) * 100m, 2);

        var lag = now - (last.OpenTime + interval);
        var lagSeconds = (int)Math.Max(0, Math.Floor(lag.TotalSeconds));
        var status = lag > interval + LateGrace ? DashboardStatus.Late : DashboardStatus.Ok;

        return new DashboardSnapshot(
            timeframe, status, lagSeconds, last.OpenTime,
            last.Close, change, inWindow.Max(c => c.High), inWindow.Min(c => c.Low), inWindow.Sum(c => c.Volume),
            [.. candles.TakeLast(SparklinePoints).Select(c => c.Close)]);
    }
}
