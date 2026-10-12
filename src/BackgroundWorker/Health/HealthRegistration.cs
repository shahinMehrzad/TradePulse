using System.Text.Json;
using Domain.Enums;
using Infrastructure.Configurations;
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
    /// Registers the state scoreboard, one check per connection this worker actually uses, and the probe worker.
    /// Components that are not configured (no spot symbols, no liquidations, ...) get no check, so they cannot raise false alarms.
    /// </summary>
    public static IServiceCollection AddWorkerHealth(this IServiceCollection services, IConfiguration configuration)
    {
        var health = configuration.GetSection("Health").Get<HealthConfigurations>() ?? new HealthConfigurations();
        var exchange = configuration.GetSection(nameof(BinanceExchangeConfigurations)).Get<BinanceExchangeConfigurations>() ?? new();
        var futuresData = configuration.GetSection(nameof(BinanceFuturesDataConfigurations)).Get<BinanceFuturesDataConfigurations>() ?? new();
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

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

        if (exchange.Symbols.Values.Any(s => s.MarketType == MarketTypeEnum.Spot))
            checks.AddTypeActivatedCheck<RequestHealthCheck>(HealthComponents.SpotRest, HealthComponents.SpotRest);

        if (exchange.Symbols.Count > 0)
            checks.AddTypeActivatedCheck<RequestHealthCheck>(HealthComponents.CandleRequests, HealthComponents.CandleRequests);

        if (futuresData.FundingRates.Count > 0)
            checks.AddTypeActivatedCheck<RequestHealthCheck>(HealthComponents.FundingRateRequests, HealthComponents.FundingRateRequests);

        if (futuresData.OpenInterests.Count > 0)
            checks.AddTypeActivatedCheck<RequestHealthCheck>(HealthComponents.OpenInterestRequests, HealthComponents.OpenInterestRequests);

        if (futuresData.Liquidations.Count > 0)
        {
            // Liquidations are bursty, so silence only degrades; the busy probe stream above is what proves the socket is alive.
            checks.AddTypeActivatedCheck<WebSocketHealthCheck>(HealthComponents.LiquidationStream, HealthComponents.LiquidationStream,
                new WebSocketHealthOptions(StartupGrace, UnhealthyAfterDisconnected,
                    MaxSilence: TimeSpan.FromMinutes(health.LiquidationMaxSilenceMinutes), SilenceStatus: HealthStatus.Degraded));
        }

        services.Configure<HealthCheckPublisherOptions>(o =>
        {
            o.Delay = TimeSpan.FromSeconds(15);
            o.Period = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IHealthCheckPublisher, LoggingHealthCheckPublisher>();

        return services;
    }

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
