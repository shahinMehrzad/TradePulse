using BackgroundWorker.CandleSeries;
using Domain.Enums;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BackgroundWorker.Health;

/// <summary>
/// Whether the worker knows what to collect. An empty table is a legitimate but unusual state (the admin chose nothing,
/// so the collector idles), hence degraded; a database that cannot be read is unhealthy.
/// </summary>
public sealed class CandleSeriesHealthCheck(CandleSeriesProvider provider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object> { ["status"] = provider.Status.ToString(), ["series"] = provider.Current.Count };

        return Task.FromResult(provider.Status switch
        {
            CandleSeriesStatus.Loaded => HealthCheckResult.Healthy($"Collecting {provider.Current.Count} candle series", data),
            CandleSeriesStatus.WaitingForConfiguration => HealthCheckResult.Degraded("No candle series are configured; the collector is idle", data: data),
            // Only the exception type is reported; messages can contain host names.
            CandleSeriesStatus.Failed => HealthCheckResult.Unhealthy($"Cannot read the candle series ({provider.LastError})", data: data),
            _ => HealthCheckResult.Degraded("Reading the candle series", data: data),
        });
    }
}

/// <summary>
/// Runs <paramref name="inner"/> only while a series of <paramref name="market"/> is being collected; otherwise the
/// component is simply not in use and cannot be a problem. Because the worker opens its connections only once the series
/// are known, "never connected" is judged from that moment instead of from process start.
/// </summary>
public sealed class MarketGatedHealthCheck(
    IHealthCheck inner, CandleSeriesProvider series, MarketTypeEnum market, TimeSpan startupGrace, TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!series.Current.Any(s => s.MarketType == market))
            return HealthCheckResult.Healthy($"No {market} candle series configured");

        var result = await inner.CheckHealthAsync(context, cancellationToken);

        if (result.Status == HealthStatus.Unhealthy
            && series.LoadedAt is { } loadedAt
            && timeProvider.GetUtcNow().UtcDateTime - loadedAt <= startupGrace)
        {
            return HealthCheckResult.Degraded(result.Description, data: result.Data);
        }

        return result;
    }
}
