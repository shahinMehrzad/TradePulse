using BackgroundWorker.Health;
using BackgroundWorker.UnitTests.CandleSeries;
using BackgroundWorker.UnitTests.SymbolData;
using Domain.Enums;
using Infrastructure.Configurations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackgroundWorker.UnitTests.Health;

public class HealthRegistrationTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1",
            }.Concat(values).ToDictionary())
            .Build();

    private static async Task<ServiceProvider> BuildAsync(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // The series are chosen at run time (in the database); a provider that already knows one futures and one spot series stands for that.
        services.AddSingleton(await CandleSeriesTestSupport.LoadedProviderAsync(
            TimeProvider.System,
            TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H),
            TestPlans.Binance(MarketTypeEnum.Spot, SymbolEnum.BTCUSDT, TimeFrameEnum._1H)));

        services.AddSingleton(await SymbolDataTestSupport.LoadedProviderAsync(
            TimeProvider.System, SymbolDataTestSupport.Binance(funding: true, liquidation: true)));

        services.AddWorkerHealth(configuration);
        return services.BuildServiceProvider();
    }

    private static async Task<HashSet<string>> RegisteredChecksAsync(IConfiguration configuration)
    {
        await using var provider = await BuildAsync(configuration);
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);
        return report.Entries.Keys.ToHashSet();
    }

    [Fact]
    public async Task EveryRegisteredCheckCanBeBuiltAndRun()
    {
        // AddTypeActivatedCheck only fails when the check is created, so run the real service end to end.
        await using var provider = await BuildAsync(Config(new()));

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.All(report.Entries.Values, e => Assert.NotNull(e.Description));
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["database"].Status);
        // What to collect is chosen at run time (in the database), so every component has its check from the start; the ones
        // that depend on the choice only judge what is actually collected.
        Assert.Equal(
            new[]
            {
                "binance-candle-stream-futures", "binance-candle-stream-spot", "binance-futures-rest", "binance-futures-websocket",
                "binance-liquidation-stream", "binance-spot-rest", "candle-freshness", "candle-gaps", "candle-requests", "candle-series", "database",
                "funding-rate-requests", "open-interest-requests", "symbol-data",
            },
            report.Entries.Keys.Order());
    }

    [Fact]
    public async Task WithoutAnyLiquidationChosenTheLiquidationStreamIsNotInUseAndNotAProblem()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(await CandleSeriesTestSupport.LoadedProviderAsync(
            TimeProvider.System, TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H)));
        services.AddSingleton(await SymbolDataTestSupport.LoadedProviderAsync(
            TimeProvider.System, SymbolDataTestSupport.Binance(funding: true, liquidation: false)));
        services.AddWorkerHealth(Config(new()));
        await using var provider = services.BuildServiceProvider();

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, report.Entries["binance-liquidation-stream"].Status);
        Assert.Equal("No liquidations configured", report.Entries["binance-liquidation-stream"].Description);
    }

    [Fact]
    public async Task WithoutLiveCandlesThereAreNoCandleStreamChecks()
    {
        var checks = await RegisteredChecksAsync(Config(new() { ["BinanceExchangeConfigurations:LiveCandles"] = "false" }));

        Assert.DoesNotContain("binance-candle-stream-futures", checks);
        Assert.DoesNotContain("binance-candle-stream-spot", checks);
        Assert.Contains("candle-series", checks);
    }

    [Fact]
    public void MissingConnectionStringFailsFast()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddWorkerHealth(new ConfigurationBuilder().Build()));
    }

    // ---- publisher ----

    private sealed class ListLogger : ILogger<LoggingHealthCheckPublisher>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static HealthReport Report(HealthStatus status, string? description = null) =>
        new(new Dictionary<string, HealthReportEntry>
        {
            ["probe"] = new(status, description, TimeSpan.Zero, null, null),
        }, TimeSpan.Zero);

    [Fact]
    public async Task Publisher_LogsOnlyWhenTheStatusChanges()
    {
        var logger = new ListLogger();
        var publisher = new LoggingHealthCheckPublisher(logger);
        var ct = TestContext.Current.CancellationToken;

        await publisher.PublishAsync(Report(HealthStatus.Healthy), ct);
        await publisher.PublishAsync(Report(HealthStatus.Healthy), ct);
        await publisher.PublishAsync(Report(HealthStatus.Unhealthy, "socket down"), ct);
        await publisher.PublishAsync(Report(HealthStatus.Unhealthy, "socket down"), ct);
        await publisher.PublishAsync(Report(HealthStatus.Healthy), ct);

        Assert.Equal([LogLevel.Information, LogLevel.Error, LogLevel.Information], logger.Entries.Select(e => e.Level));
        Assert.Contains("socket down", logger.Entries[1].Message);
        Assert.Contains("was Unhealthy", logger.Entries[2].Message);
    }

    [Fact]
    public async Task Publisher_LogsDegradedAsAWarning()
    {
        var logger = new ListLogger();

        await new LoggingHealthCheckPublisher(logger).PublishAsync(Report(HealthStatus.Degraded, "slow"), TestContext.Current.CancellationToken);

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }
}
