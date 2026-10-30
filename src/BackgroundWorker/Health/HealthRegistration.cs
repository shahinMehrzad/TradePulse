using Infrastructure;
using System.Text.Json;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Collection.Candles;
using BackgroundWorker.SymbolData;
using Infrastructure.Health;
using Domain.Enums;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.Health;

public static class HealthRegistration
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan UnhealthyAfterDisconnected = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Registers the state scoreboard, one check per connection this worker can use, and the probe worker.
    /// What is not configured (no liquidations, ...) gets no check. The candle series are chosen at run time, so the checks
    /// that depend on them (the per-market streams) are always registered but only judge a market that has a series.
    /// Needs a <see cref="CandleSeriesProvider"/> and a <see cref="SymbolDataProvider"/> in the container.
    /// </summary>
    public static IServiceCollection AddWorkerHealth(this IServiceCollection services, IConfiguration configuration)
    {
        var health = configuration.GetSection("Health").Get<HealthConfigurations>() ?? new HealthConfigurations();
        var liveCandles = configuration.GetSection(LiveCandleSettings.SectionName).Get<LiveCandleSettings>() ?? new();
        var connectionString = configuration.RequiredConnectionString("DefaultConnection");

        services.Configure<HealthConfigurations>(configuration.GetSection("Health"));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<WorkerHealthState>();
        services.AddHostedService<BinanceConnectivityWorker>();

        var checks = services.AddHealthChecks();
        checks.AddTypeActivatedCheck<DatabaseHealthCheck>("database", connectionString);

        // The probe always runs: it pings futures REST and holds a busy stream open to prove the socket path works.
        checks.AddTypeActivatedCheck<RequestHealthCheck>(HealthComponents.FuturesRest, HealthComponents.FuturesRest);
        checks.AddTypeActivatedCheck<WebSocketHealthCheck>(HealthComponents.FuturesWebSocket, HealthComponents.FuturesWebSocket,
            new WebSocketHealthOptions(StartupGrace, UnhealthyAfterDisconnected,
                MaxSilence: TimeSpan.FromSeconds(60), SilenceStatus: HealthStatus.Unhealthy));

        // Whether the worker knows what to collect, and the checks that only matter once it does. These are harmless while
        // idle: a request component that has not made a request is healthy, and the gated checks report "not in use".
        checks.AddTypeActivatedCheck<CandleSeriesHealthCheck>(HealthComponents.CandleSeries);
        checks.AddTypeActivatedCheck<SymbolDataHealthCheck>(HealthComponents.SymbolData);
        checks.AddTypeActivatedCheck<RequestHealthCheck>(HealthComponents.SpotRest, HealthComponents.SpotRest);
        checks.AddTypeActivatedCheck<RequestHealthCheck>(HealthComponents.CandleRequests, HealthComponents.CandleRequests);

        // Data-level check: connections can be fine while candles stop being stored.
        services.AddSingleton<CandleFreshnessMonitor>();
        services.AddSingleton<OpenGaps>();
        checks.AddTypeActivatedCheck<CandleGapsHealthCheck>(HealthComponents.CandleGaps);
        checks.AddTypeActivatedCheck<CandleFreshnessHealthCheck>(HealthComponents.CandleFreshness);

        if (liveCandles.LiveCandles)
        {
            // Kline streams also push updates for the still-open candle, so even a quiet interval is never silent for long.
            var streamOptions = new WebSocketHealthOptions(StartupGrace, UnhealthyAfterDisconnected,
                MaxSilence: TimeSpan.FromMinutes(5), SilenceStatus: HealthStatus.Degraded);

            AddCandleStreamCheck(checks, HealthComponents.CandleStreamFutures, MarketTypeEnum.Futures, streamOptions);
            AddCandleStreamCheck(checks, HealthComponents.CandleStreamSpot, MarketTypeEnum.Spot, streamOptions);
        }

        // Which symbols collect their funding rate or liquidations, and which series collect open interest, is chosen at run
        // time (in the database), so these are always registered. A request component that has not made a request is healthy.
        checks.AddTypeActivatedCheck<RequestHealthCheck>(HealthComponents.FundingRateRequests, HealthComponents.FundingRateRequests);
        checks.AddTypeActivatedCheck<RequestHealthCheck>(HealthComponents.OpenInterestRequests, HealthComponents.OpenInterestRequests);

        // Liquidations are bursty, so silence only degrades; the busy probe stream above is what proves the socket is alive.
        // The stream is only judged once some symbol asks for liquidations.
        var liquidationOptions = new WebSocketHealthOptions(StartupGrace, UnhealthyAfterDisconnected,
            MaxSilence: TimeSpan.FromMinutes(health.LiquidationMaxSilenceMinutes), SilenceStatus: HealthStatus.Degraded);
        checks.Add(new HealthCheckRegistration(
            HealthComponents.LiquidationStream,
            provider =>
            {
                var symbolData = provider.GetRequiredService<SymbolDataProvider>();
                return new GatedHealthCheck(
                    ActivatorUtilities.CreateInstance<WebSocketHealthCheck>(provider, HealthComponents.LiquidationStream, liquidationOptions),
                    () => symbolData.Current.ForExchange(ExchangeEnum.Binance).Any(p => p.CollectLiquidation),
                    "No liquidations configured",
                    () => symbolData.LoadedAt,
                    StartupGrace,
                    provider.GetRequiredService<TimeProvider>());
            },
            failureStatus: null,
            tags: null));

        services.Configure<HealthCheckPublisherOptions>(o =>
        {
            o.Delay = TimeSpan.FromSeconds(15);
            o.Period = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IHealthCheckPublisher, LoggingHealthCheckPublisher>();

        return services;
    }

    private static void AddCandleStreamCheck(IHealthChecksBuilder checks, string component, MarketTypeEnum market, WebSocketHealthOptions options) =>
        checks.Add(new HealthCheckRegistration(
            component,
            provider => new MarketGatedHealthCheck(
                ActivatorUtilities.CreateInstance<WebSocketHealthCheck>(provider, component, options),
                provider.GetRequiredService<CandleSeriesProvider>(),
                market,
                StartupGrace,
                provider.GetRequiredService<TimeProvider>()),
            failureStatus: null,
            tags: null));

    /// <summary>Maps <c>/health</c> (all checks, JSON) and <c>/health/live</c> (process is up, no checks).</summary>
    public static void UseHealthEndpoints(this IApplicationBuilder app)
    {
        app.UseRouting();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapHealthChecks("/health", new HealthCheckOptions { ResponseWriter = WriteJsonAsync });
            endpoints.MapGet("/health/live", () => Results.Text("alive"));
        });
    }

    private static Task WriteJsonAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var body = new
        {
            status = report.Status.ToString(),
            totalDurationMs = (int)report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data,
            }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(body, new JsonSerializerOptions { WriteIndented = true }));
    }
}

/// <summary>
/// Writes to the log only when the overall status changes (and once at startup), so a healthy worker stays quiet
/// and a problem is visible without anybody polling the endpoint.
/// </summary>
public sealed class LoggingHealthCheckPublisher(ILogger<LoggingHealthCheckPublisher> logger) : IHealthCheckPublisher
{
    private HealthStatus? _last;

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        if (_last == report.Status)
            return Task.CompletedTask;

        var previous = _last;
        _last = report.Status;

        var problems = report.Entries
            .Where(e => e.Value.Status != HealthStatus.Healthy)
            .Select(e => $"{e.Key}: {e.Value.Status} ({e.Value.Description})")
            .ToList();

        switch (report.Status)
        {
            case HealthStatus.Healthy:
                logger.LogInformation("Health is {Status}{Previous}", report.Status, previous is null ? "" : $" (was {previous})");
                break;
            case HealthStatus.Degraded:
                logger.LogWarning("Health is {Status}: {Problems}", report.Status, string.Join("; ", problems));
                break;
            default:
                logger.LogError("Health is {Status}: {Problems}", report.Status, string.Join("; ", problems));
                break;
        }

        return Task.CompletedTask;
    }
}
