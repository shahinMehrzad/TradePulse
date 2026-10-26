using Application;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Collection.Candles;
using BackgroundWorker.Collection.FundingRates;
using BackgroundWorker.Collection.Liquidations;
using BackgroundWorker.Collection.OpenInterests;
using BackgroundWorker.Health;
using BackgroundWorker.SymbolData;
using Infrastructure;
using Infrastructure.Exchange.Binance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BackgroundWorker;

public static class CollectorRegistration
{
    /// <summary>
    /// Everything the collector runs on. What to collect is read from the database by the two providers (registered first, so they
    /// start reading before the workers wait for them); how to reach the exchange is the Binance adapter.
    /// </summary>
    public static IServiceCollection AddCollector(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddApplicationServices();
        services.AddInfrastructureServices(configuration);
        services.AddBinanceExchange(configuration);
        services.Configure<LiveCandleSettings>(configuration.GetSection(LiveCandleSettings.SectionName));

        services.AddSingleton<ICandleSeriesSource, DatabaseCandleSeriesSource>();
        services.AddSingleton<CandleSeriesProvider>();
        services.AddHostedService(provider => provider.GetRequiredService<CandleSeriesProvider>());
        services.AddSingleton<ISymbolDataSource, DatabaseSymbolDataSource>();
        services.AddSingleton<SymbolDataProvider>();
        services.AddHostedService(provider => provider.GetRequiredService<SymbolDataProvider>());

        services.AddSingleton<CandleSyncCoordinator>();
        services.AddSingleton<GapRepairSchedule>();
        services.AddSingleton<CollectionStats>();
        services.AddWorkerHealth(configuration);

        services.AddHostedService<BinanceCandleWorker>();
        services.AddHostedService<BinanceLiveCandleWorker>();
        services.AddHostedService<BinanceFundingRateWorker>();
        services.AddHostedService<BinanceOpenInterestWorker>();
        services.AddHostedService<BinanceLiquidationWorker>();
        services.AddHostedService<CollectorSummaryWorker>();

        return services;
    }
}
