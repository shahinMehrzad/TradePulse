using Application.Dtos.Candles;
using Application.Dtos.Dashboard;
using Application.Interfaces;
using Application.Queries.Candles;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Extensions;
using Domain.Repositories;
using Domain.ValueObjects;

namespace Application.Queries.Dashboard;

public readonly record struct GetDashboardSeriesQuery(MarketTypeEnum? Market);

public sealed class GetDashboardSeriesQueryHandler(ICandleWorkerConfigurationRepository configurations,
    ICandleReader candleReader,  TimeProvider timeProvider) : ICommandHandler<GetDashboardSeriesQuery, DashboardSeriesResponse>
{
    public const int SparklinePoints = 24;    
    public static readonly TimeSpan LateGrace = TimeSpan.FromSeconds(60);
    private const int MaxParallelReads = 8;
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    public async Task<DashboardSeriesResponse> HandleAsync(GetDashboardSeriesQuery request, CancellationToken cancellationToken)
    {
        var rows = await configurations.GetConfigurationList(cancellationToken);

        var cards = rows
            .Where(c => Enum.IsDefined((ExchangeEnum)c.ExchangeId) && Enum.IsDefined((MarketTypeEnum)c.MarketId)
                        && Enum.IsDefined((SymbolEnum)c.SymbolId) && Enum.IsDefined((TimeFrameEnum)c.TimeFrameId))
            .Where(c => request.Market is not { } market || c.MarketId == (int)market)
            .GroupBy(c => (c.ExchangeId, c.MarketId, c.SymbolId))
            .OrderBy(g => g.Key.MarketId).ThenBy(g => ((SymbolEnum)g.Key.SymbolId).ToString(), StringComparer.Ordinal)
            .Select(g => (
                Exchange: (ExchangeEnum)g.Key.ExchangeId,
                Market: (MarketTypeEnum)g.Key.MarketId,
                Symbol: (SymbolEnum)g.Key.SymbolId,
                TimeFrames: g.Select(c => (TimeFrameEnum)c.TimeFrameId).Distinct().Order().ToArray()))
            .ToArray();

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var items = new DashboardSeriesItem[cards.Length];

        await Parallel.ForEachAsync(
            Enumerable.Range(0, cards.Length),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelReads, CancellationToken = cancellationToken },
            async (i, ct) =>
            {
                var card = cards[i];
                var series = new CandleSeries(card.Exchange, card.Market, card.Symbol, card.TimeFrames[0]);
                items[i] = new DashboardSeriesItem(
                    card.Exchange.ToRouteName(), card.Market.ToRouteName(), card.Symbol.ToRouteName(),
                    [.. card.TimeFrames.Select(t => t.ToRouteName())], series.Timeframe.ToRouteName(),
                    await SnapshotAsync(series, now, ct));
            });

        return new DashboardSeriesResponse(now, items);
    }

    private async Task<DashboardSnapshot> SnapshotAsync(CandleSeries series, DateTime now, CancellationToken cancellationToken)
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
