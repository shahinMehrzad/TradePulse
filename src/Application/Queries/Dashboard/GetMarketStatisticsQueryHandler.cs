using Application.Dtos.Dashboard;
using Application.Interfaces;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Extensions;
using Domain.Repositories;
using Domain.Services;
using Domain.ValueObjects;

namespace Application.Queries.Dashboard;

public readonly record struct GetMarketStatisticsQuery(ExchangeEnum Exchange, MarketTypeEnum Market, string Symbol, TimeFrameEnum Timeframe, int? Points = null);

/// <summary>
/// The newest rows of the long/short ratio and the taker volume of one series (the symbol is the route's name for it, as in
/// <see cref="GetDashboardSeriesDetailQuery"/>). A table that does not exist yet or has no rows gives an empty list, never an error: the
/// collector creates it when it first collects, and the exchange keeps only about 30 days of these, so a new series starts empty.
/// </summary>
public sealed class GetMarketStatisticsQueryHandler(
    ICandleWorkerConfigurationRepository configurations,
    IMarketDataRepository<LongShortRatio> ratioTables, ITimeSeriesReader<LongShortRatio> ratios,
    IMarketDataRepository<TakerVolume> takerTables, ITimeSeriesReader<TakerVolume> takers) : ICommandHandler<GetMarketStatisticsQuery, MarketStatisticsResponse>
{
    public const int DefaultPoints = 48;
    public const int MaxPoints = 200;

    public async Task<MarketStatisticsResponse> HandleAsync(GetMarketStatisticsQuery request, CancellationToken cancellationToken)
    {
        var row = (await configurations.GetConfigurationList(cancellationToken))
            .FirstOrDefault(c => c.TimeFrameId == (int)request.Timeframe && DashboardListings.Matches(c.SymbolListing, request.Exchange, request.Market, request.Symbol))
            ?? throw new ConfigurationNotFoundException($"The {request.Timeframe.ToRouteName()} time frame is not collected for this series.");

        var symbol = new ExchangeSymbol(row.SymbolListing!.ExchangeSymbol);
        var timeframe = request.Timeframe.ToRouteName();

        if (!row.CollectMarketStatistics)
            return new MarketStatisticsResponse(request.Exchange.ToRouteName(), request.Market.ToRouteName(), request.Symbol, timeframe, false, [], []);

        var points = Math.Clamp(request.Points ?? DefaultPoints, 2, MaxPoints);

        var longShortTable = TableNameRegistry.GetLongShortRatioTableName(request.Exchange, symbol.Value, request.Timeframe);
        var takerTable = TableNameRegistry.GetTakerVolumeTableName(request.Exchange, symbol.Value, request.Timeframe);

        var longShort = await ratioTables.TableExistsAsync(longShortTable, cancellationToken)
            ? [.. (await ratios.GetNewestAsync(longShortTable, points, cancellationToken)).Select(r => new LongShortPoint(r.Time, r.Ratio, r.LongAccount, r.ShortAccount))]
            : new List<LongShortPoint>();

        var taker = await takerTables.TableExistsAsync(takerTable, cancellationToken)
            ? [.. (await takers.GetNewestAsync(takerTable, points, cancellationToken)).Select(t => new TakerVolumePoint(t.Time, t.BuySellRatio, t.BuyVolume, t.SellVolume))]
            : new List<TakerVolumePoint>();

        return new MarketStatisticsResponse(request.Exchange.ToRouteName(), request.Market.ToRouteName(), request.Symbol, timeframe, true, longShort, taker);
    }
}
