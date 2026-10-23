using Application.Dtos.Dashboard;
using Application.Interfaces;
using Domain.Enums;
using Domain.Extensions;
using Domain.Repositories;
using Domain.ValueObjects;

namespace Application.Queries.Dashboard;

public readonly record struct GetDashboardSeriesQuery(MarketTypeEnum? Market);

public sealed class GetDashboardSeriesQueryHandler(ICandleWorkerConfigurationRepository configurations,
    DashboardSnapshotBuilder snapshots, TimeProvider timeProvider) : ICommandHandler<GetDashboardSeriesQuery, DashboardSeriesResponse>
{
    private const int MaxParallelReads = 8;

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
                    await snapshots.BuildAsync(series, now, ct));
            });

        return new DashboardSeriesResponse(now, items);
    }
}
