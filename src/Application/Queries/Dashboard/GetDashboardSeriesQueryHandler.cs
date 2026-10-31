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

        // The dashboard still routes by the old symbol names, so a symbol that is not one of them has no card until it gets a route of its own.
        var cards = rows
            .Select(c => (Row: c, Listing: c.SymbolListing))
            .Where(x => x.Listing is not null && Enum.IsDefined((ExchangeEnum)x.Listing.ExchangeId) && Enum.IsDefined((MarketTypeEnum)x.Listing.MarketId)
                        && Enum.TryParse<SymbolEnum>(x.Listing.ExchangeSymbol, out var symbol) && Enum.IsDefined(symbol)
                        && Enum.IsDefined((TimeFrameEnum)x.Row.TimeFrameId))
            .Where(x => request.Market is not { } market || x.Listing!.MarketId == (int)market)
            .GroupBy(x => (x.Listing!.ExchangeId, x.Listing.MarketId, x.Listing.ExchangeSymbol))
            .OrderBy(g => g.Key.MarketId).ThenBy(g => g.Key.ExchangeSymbol, StringComparer.Ordinal)
            .Select(g => (
                Exchange: (ExchangeEnum)g.Key.ExchangeId,
                Market: (MarketTypeEnum)g.Key.MarketId,
                Symbol: Enum.Parse<SymbolEnum>(g.Key.ExchangeSymbol),
                TimeFrames: g.Select(x => (TimeFrameEnum)x.Row.TimeFrameId).Distinct().Order().ToArray()))
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
                    await snapshots.BuildAsync(series, now, DashboardSnapshotBuilder.SparklinePoints, ct));
            });

        return new DashboardSeriesResponse(now, items);
    }
}
