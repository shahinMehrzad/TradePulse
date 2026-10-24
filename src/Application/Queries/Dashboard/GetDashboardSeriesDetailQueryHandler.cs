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

public readonly record struct GetDashboardSeriesDetailQuery(ExchangeEnum Exchange, MarketTypeEnum Market, SymbolEnum Symbol, TimeFrameEnum? Timeframe, int? Candles = null);

public sealed class GetDashboardSeriesDetailQueryHandler(ICandleWorkerConfigurationRepository configurations, ISymbolDataConfigurationRepository symbolData,
    IMarketDataRepository<FundingRate> fundingRates, IMarketDataRepository<OpenInterest> openInterests, IMarketDataRepository<Liquidation> liquidations,
    DashboardSnapshotBuilder snapshots, TimeProvider timeProvider) : ICommandHandler<GetDashboardSeriesDetailQuery, DashboardSeriesDetail>
{
    private static readonly TimeSpan FundingInterval = TimeSpan.FromHours(8);

    public async Task<DashboardSeriesDetail> HandleAsync(GetDashboardSeriesDetailQuery request, CancellationToken cancellationToken)
    {
        var rows = (await configurations.GetConfigurationList(cancellationToken))
            .Where(c => c.ExchangeId == (int)request.Exchange && c.MarketId == (int)request.Market && c.SymbolId == (int)request.Symbol
                        && Enum.IsDefined((TimeFrameEnum)c.TimeFrameId))
            .ToList();
        var timeFrames = rows.Select(c => (TimeFrameEnum)c.TimeFrameId).Distinct().Order().ToArray();
        if (timeFrames.Length == 0)
            throw new ConfigurationNotFoundException("That series is not collected.");

        var chosen = request.Timeframe ?? timeFrames[0];
        if (!timeFrames.Contains(chosen))
            throw new ConfigurationNotFoundException($"The {chosen.ToRouteName()} time frame is not collected for this series.");

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var series = new CandleSeries(request.Exchange, request.Market, request.Symbol, chosen);

        var related = request.Market == MarketTypeEnum.Futures
            ? await RelatedAsync(request, rows.First(c => c.TimeFrameId == (int)chosen), chosen, now, cancellationToken)
            : [];

        return new DashboardSeriesDetail(
            request.Exchange.ToRouteName(), request.Market.ToRouteName(), request.Symbol.ToRouteName(),
            [.. timeFrames.Select(t => t.ToRouteName())], timeFrames[0].ToRouteName(),
            await snapshots.BuildAsync(series, now, request.Candles ?? DashboardSnapshotBuilder.SparklinePoints, cancellationToken), related);
    }

    private async Task<IReadOnlyList<DashboardRelatedData>> RelatedAsync(
        GetDashboardSeriesDetailQuery request, CandleWorkerConfiguration chosenRow, TimeFrameEnum chosen, DateTime now, CancellationToken cancellationToken)
    {
        var settings = (await symbolData.GetAll(cancellationToken))
            .FirstOrDefault(s => s.ExchangeId == (int)request.Exchange && s.MarketId == (int)request.Market && s.SymbolId == (int)request.Symbol);

        var related = new List<DashboardRelatedData>(3);

        if (settings is { CollectFundingRate: true })
            related.Add(await ReadAsync(MarketDataTypeEnum.FundingRate, TableNameRegistry.GetFundingRateTableName(request.Exchange, request.Symbol),
                fundingRates, now, FundingInterval + DashboardSnapshotBuilder.LateGrace, cancellationToken));

        if (chosenRow.CollectOpenInterest)
            related.Add(await ReadAsync(MarketDataTypeEnum.OpenInterest, TableNameRegistry.GetOpenInterestTableName(request.Exchange, request.Symbol, chosen),
                openInterests, now, TimeSpan.FromSeconds((int)chosen) + DashboardSnapshotBuilder.LateGrace, cancellationToken));

        // Liquidations are a sample (no history, one event per second at most): quiet markets have long gaps, so never "late".
        if (settings is { CollectLiquidation: true })
            related.Add(await ReadAsync(MarketDataTypeEnum.Liquidation, TableNameRegistry.GetLiquidationTableName(request.Exchange, request.Symbol),
                liquidations, now, null, cancellationToken));

        return related;
    }

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
