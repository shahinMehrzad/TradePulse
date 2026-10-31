using Application.Exchange;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Health;
using Domain.Entities;
using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.Collection.MarketStatistics;

/// <summary>Long/short ratio of all accounts, per period.</summary>
public sealed class BinanceLongShortRatioWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BinanceLongShortRatioWorker> logger,
    CandleSeriesProvider seriesProvider,
    IPeriodStatisticGateway<LongShortRatio> gateway,
    WorkerHealthState health,
    CollectionStats stats)
    : PeriodStatisticWorker<LongShortRatio>(scopeFactory, logger, seriesProvider, gateway, health, stats)
{
    protected override string Name => "long/short ratio";

    protected override MarketDataTypeEnum DataType => MarketDataTypeEnum.LongShortRatio;

    protected override string HealthComponent => HealthComponents.LongShortRatioRequests;

    protected override string TableName(Domain.ValueObjects.CandleSeries series) =>
        TableNameRegistry.GetLongShortRatioTableName(series.Exchange, series.Symbol.Value, series.Timeframe);

    protected override DateTime TimeOf(LongShortRatio row) => row.Time;
}

/// <summary>Taker buy and sell volume, per period.</summary>
public sealed class BinanceTakerVolumeWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BinanceTakerVolumeWorker> logger,
    CandleSeriesProvider seriesProvider,
    IPeriodStatisticGateway<TakerVolume> gateway,
    WorkerHealthState health,
    CollectionStats stats)
    : PeriodStatisticWorker<TakerVolume>(scopeFactory, logger, seriesProvider, gateway, health, stats)
{
    protected override string Name => "taker volume";

    protected override MarketDataTypeEnum DataType => MarketDataTypeEnum.TakerVolume;

    protected override string HealthComponent => HealthComponents.TakerVolumeRequests;

    protected override string TableName(Domain.ValueObjects.CandleSeries series) =>
        TableNameRegistry.GetTakerVolumeTableName(series.Exchange, series.Symbol.Value, series.Timeframe);

    protected override DateTime TimeOf(TakerVolume row) => row.Time;
}
