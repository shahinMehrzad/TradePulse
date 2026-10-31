using Application.Dtos.Dashboard;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Extensions;
using Domain.Repositories;
using Domain.Services;
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

        // One card per symbol of an exchange market. The symbol is named in the route by its table-name part (btcusdt), the same
        // for every spelling that gives the same tables.
        var cards = rows
            .Select(c => (Row: c, Listing: c.SymbolListing, Part: SymbolPart(c.SymbolListing)))
            .Where(x => x.Listing is not null && x.Part is not null && Enum.IsDefined((TimeFrameEnum)x.Row.TimeFrameId))
            .Where(x => request.Market is not { } market || x.Listing!.MarketId == (int)market)
            .GroupBy(x => (x.Listing!.ExchangeId, x.Listing.MarketId, x.Part))
            .OrderBy(g => g.Key.MarketId).ThenBy(g => g.Key.Part, StringComparer.Ordinal)
            .Select(g => (
                Exchange: (ExchangeEnum)g.Key.ExchangeId,
                Market: (MarketTypeEnum)g.Key.MarketId,
                Part: g.Key.Part!,
                Symbol: new ExchangeSymbol(g.First().Listing!.ExchangeSymbol),
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
                    card.Exchange.ToRouteName(), card.Market.ToRouteName(), card.Part,
                    [.. card.TimeFrames.Select(t => t.ToRouteName())], series.Timeframe.ToRouteName(),
                    await snapshots.BuildAsync(series, now, DashboardSnapshotBuilder.SparklinePoints, ct));
            });

        return new DashboardSeriesResponse(now, items);
    }

    /// <summary>How the route names the symbol of a listing; null when the exchange or market is not one the collector knows or the name cannot be a table name.</summary>
    private static string? SymbolPart(SymbolListing? listing) =>
        listing is not null && Enum.IsDefined((ExchangeEnum)listing.ExchangeId) && Enum.IsDefined((MarketTypeEnum)listing.MarketId)
        && TableNameRegistry.TryGetSymbolPart((ExchangeEnum)listing.ExchangeId, (MarketTypeEnum)listing.MarketId, listing.ExchangeSymbol, out var part, out _)
            ? part
            : null;
}
