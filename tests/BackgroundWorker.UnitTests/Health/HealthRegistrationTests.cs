using BackgroundWorker.Health;
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

    private static ServiceProvider Build(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddWorkerHealth(configuration);
        return services.BuildServiceProvider();
    }

    private static async Task<HashSet<string>> RegisteredChecksAsync(IConfiguration configuration)
    {
        await using var provider = Build(configuration);
        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);
        return report.Entries.Keys.ToHashSet();
    }

    [Fact]
    public async Task EveryRegisteredCheckCanBeBuiltAndRun()
    {
        // AddTypeActivatedCheck only fails when the check is created, so run the real service end to end.
        var config = Config(new()
        {
            ["BinanceExchangeConfigurations:Symbols:a:Name"] = "BTCUSDT",
            ["BinanceExchangeConfigurations:Symbols:a:Interval"] = "_1H",
            ["BinanceExchangeConfigurations:Symbols:a:MarketType"] = "Spot",
            ["BinanceFuturesDataConfigurations:FundingRates:a:Name"] = "BTCUSDT",
            ["BinanceFuturesDataConfigurations:OpenInterests:a:Name"] = "BTCUSDT",
            ["BinanceFuturesDataConfigurations:Liquidations:a:Name"] = "BTCUSDT",
        });
        await using var provider = Build(config);

        var report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.All(report.Entries.Values, e => Assert.NotNull(e.Description));
        Assert.Equal(HealthStatus.Unhealthy, report.Entries["database"].Status);
        Assert.Equal(
            new[]
            {
                "binance-futures-rest", "binance-futures-websocket", "binance-liquidation-stream", "binance-spot-rest",
                "candle-requests", "database", "funding-rate-requests", "open-interest-requests",
            },
            report.Entries.Keys.Order());
    }

    [Fact]
    public async Task OnlyConfiguredComponentsGetAChecks()
    {
        // No spot symbols, no futures data: nothing to monitor beyond the always-on probe and the database.
        var checks = await RegisteredChecksAsync(Config(new()
        {
            ["BinanceExchangeConfigurations:Symbols:a:Name"] = "BTCUSDT",
            ["BinanceExchangeConfigurations:Symbols:a:Interval"] = "_1H",
            ["BinanceExchangeConfigurations:Symbols:a:MarketType"] = "Futures",
        }));

        Assert.Equal(
            new[] { "binance-futures-rest", "binance-futures-websocket", "candle-requests", "database" },
            checks.Order());
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
