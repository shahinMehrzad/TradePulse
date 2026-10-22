using Application;
using BackgroundWorker;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Health;
using Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
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

    services.AddTransient<DatabaseSeeder>();

    // Which candle series to collect comes from the database. Registered first so it starts reading before the workers wait for it.
    services.AddSingleton<ICandleSeriesSource, DatabaseCandleSeriesSource>();
    services.AddSingleton<CandleSeriesProvider>();
    services.AddHostedService(provider => provider.GetRequiredService<CandleSeriesProvider>());
    services.AddSingleton<BinanceRateLimiters>();
    services.AddSingleton<CandleSyncCoordinator>();
    services.AddWorkerHealth(context.Configuration);

    services.AddHostedService<BinanceCandleWorker>();
    services.AddHostedService<BinanceLiveCandleWorker>();
    services.AddHostedService<BinanceFundingRateWorker>();
    services.AddHostedService<BinanceOpenInterestWorker>();
    services.AddHostedService<BinanceLiquidationWorker>();
}).Build();


using (var scope = host.Services.CreateScope())
{
    var seeder = scope.ServiceProvider.GetRequiredService<DatabaseSeeder>();
    await seeder.InitializeBinanceAsync();
}

await host.RunAsync();
