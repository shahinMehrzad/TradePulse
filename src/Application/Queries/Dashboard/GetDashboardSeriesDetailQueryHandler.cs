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

public readonly record struct GetDashboardSeriesDetailQuery(ExchangeEnum Exchange, MarketTypeEnum Market, string Symbol, TimeFrameEnum? Timeframe, int? Candles = null);

// Symbol is the route's name for it: the table-name part of the exchange's name (btcusdt).

public sealed class GetDashboardSeriesDetailQueryHandler(ICandleWorkerConfigurationRepository configurations, ISymbolDataConfigurationRepository symbolData,
    IMarketDataRepository<FundingRate> fundingRates, IMarketDataRepository<OpenInterest> openInterests, IMarketDataRepository<Liquidation> liquidations,
    DashboardSnapshotBuilder snapshots, TimeProvider timeProvider) : ICommandHandler<GetDashboardSeriesDetailQuery, DashboardSeriesDetail>
{
    private static readonly TimeSpan FundingInterval = TimeSpan.FromHours(8);

    public async Task<DashboardSeriesDetail> HandleAsync(GetDashboardSeriesDetailQuery request, CancellationToken cancellationToken)
    {
        var rows = (await configurations.GetConfigurationList(cancellationToken))
            .Where(c => IsListing(c.SymbolListing, request) && Enum.IsDefined((TimeFrameEnum)c.TimeFrameId))
            .ToList();
        var timeFrames = rows.Select(c => (TimeFrameEnum)c.TimeFrameId).Distinct().Order().ToArray();
        if (timeFrames.Length == 0)
            throw new ConfigurationNotFoundException("That series is not collected.");

        var symbol = new ExchangeSymbol(rows[0].SymbolListing!.ExchangeSymbol);
        var chosen = request.Timeframe ?? timeFrames[0];
        if (!timeFrames.Contains(chosen))
            throw new ConfigurationNotFoundException($"The {chosen.ToRouteName()} time frame is not collected for this series.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var series = new CandleSeries(request.Exchange, request.Market, symbol, chosen);

        var related = request.Market == MarketTypeEnum.Futures
            ? await RelatedAsync(request, symbol, rows.First(c => c.TimeFrameId == (int)chosen), chosen, now, cancellationToken)
            : [];

        return new DashboardSeriesDetail(
            request.Exchange.ToRouteName(), request.Market.ToRouteName(), request.Symbol,
            [.. timeFrames.Select(t => t.ToRouteName())], timeFrames[0].ToRouteName(),
            await snapshots.BuildAsync(series, now, request.Candles ?? DashboardSnapshotBuilder.SparklinePoints, cancellationToken), related);
    }

    private async Task<IReadOnlyList<DashboardRelatedData>> RelatedAsync(
        GetDashboardSeriesDetailQuery request, ExchangeSymbol symbol, CandleWorkerConfiguration chosenRow, TimeFrameEnum chosen, DateTime now, CancellationToken cancellationToken)
    {
        var settings = (await symbolData.GetAll(cancellationToken))
            .FirstOrDefault(s => IsListing(s.SymbolListing, request));

        var related = new List<DashboardRelatedData>(3);

        if (settings is { CollectFundingRate: true })
            related.Add(await ReadAsync(MarketDataTypeEnum.FundingRate, TableNameRegistry.GetFundingRateTableName(request.Exchange, symbol.Value),
                fundingRates, now, FundingInterval + DashboardSnapshotBuilder.LateGrace, cancellationToken));

        if (chosenRow.CollectOpenInterest)
            related.Add(await ReadAsync(MarketDataTypeEnum.OpenInterest, TableNameRegistry.GetOpenInterestTableName(request.Exchange, symbol.Value, chosen),
                openInterests, now, TimeSpan.FromSeconds((int)chosen) + DashboardSnapshotBuilder.LateGrace, cancellationToken));

        // Liquidations are a sample (no history, one event per second at most): quiet markets have long gaps, so never "late".
        if (settings is { CollectLiquidation: true })
            related.Add(await ReadAsync(MarketDataTypeEnum.Liquidation, TableNameRegistry.GetLiquidationTableName(request.Exchange, symbol.Value),
                liquidations, now, null, cancellationToken));

        return related;
    }

    private static bool IsListing(SymbolListing? listing, GetDashboardSeriesDetailQuery request) =>
        listing is not null && listing.ExchangeId == (int)request.Exchange && listing.MarketId == (int)request.Market
        && TableNameRegistry.TryGetSymbolPart(request.Exchange, request.Market, listing.ExchangeSymbol, out var part, out _)
        && part == request.Symbol;

    private static async Task<DashboardRelatedData> ReadAsync<T>(
        MarketDataTypeEnum dataType, string tableName, IMarketDataRepository<T> repository, DateTime now, TimeSpan? lateAfter, CancellationToken cancellationToken)
    {
        var last = await repository.TableExistsAsync(tableName, cancellationToken)
            ? await repository.GetLatestTimeAsync(tableName, cancellationToken)
            : null;

        if (last is not { } lastTime)
            return new DashboardRelatedData(dataType.ToRouteName(), DashboardStatus.Empty, null, null);

        var lag = now - lastTime;
        var status = lateAfter is { } limit && lag > limit ? DashboardStatus.Late : DashboardStatus.Ok;

        return new DashboardRelatedData(dataType.ToRouteName(), status, (int)Math.Max(0, Math.Floor(lag.TotalSeconds)), lastTime);
    }
}
