using Application;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Collection.Candles;
using BackgroundWorker.Collection.FundingRates;
using BackgroundWorker.Collection.Liquidations;
using BackgroundWorker.Collection.OpenInterests;
using Infrastructure.Exchange.Binance;
using BackgroundWorker.Health;
using BackgroundWorker.SymbolData;
using Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateDefaultBuilder(args);

var configuration = new ConfigurationBuilder()
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("appsettings.json")
    .Build();

var health = configuration.GetSection("Health").Get<HealthConfigurations>() ?? new HealthConfigurations();

if (health.Enabled)
{
    // Small Kestrel host that only serves /health, so orchestrators and humans can ask whether the worker is OK.
    builder.ConfigureWebHost(web => web
        .UseKestrel()
        .UseUrls(health.Url)
        .ConfigureServices(services => services.AddRouting())
        .Configure(app => app.UseHealthEndpoints()));
}

var host = builder.ConfigureServices((context, services) =>
{
    services.AddApplicationServices();
    services.AddInfrastructureServices(context.Configuration);
    services.AddBinanceExchange(context.Configuration);
    services.Configure<LiveCandleSettings>(context.Configuration.GetSection(LiveCandleSettings.SectionName));
    services.AddSingleton<ICandleSeriesSource, DatabaseCandleSeriesSource>();
    services.AddSingleton<CandleSeriesProvider>();
    services.AddHostedService(provider => provider.GetRequiredService<CandleSeriesProvider>());
    services.AddSingleton<ISymbolDataSource, DatabaseSymbolDataSource>();
    services.AddSingleton<SymbolDataProvider>();
    services.AddHostedService(provider => provider.GetRequiredService<SymbolDataProvider>());
    services.AddSingleton<BinanceRateLimiters>();
    services.AddSingleton<CandleSyncCoordinator>();
    services.AddWorkerHealth(context.Configuration);

    services.AddHostedService<BinanceCandleWorker>();
    services.AddHostedService<BinanceLiveCandleWorker>();
    services.AddHostedService<BinanceFundingRateWorker>();
    services.AddHostedService<BinanceOpenInterestWorker>();
    services.AddHostedService<BinanceLiquidationWorker>();
}).Build();

await host.RunAsync();
