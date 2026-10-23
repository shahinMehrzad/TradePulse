using BackgroundWorker.CandleSeries;
using BackgroundWorker.Plans;
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
            PlanStatus.Loaded => HealthCheckResult.Healthy($"Collecting {provider.Current.Count} candle series", data),
            PlanStatus.WaitingForConfiguration => HealthCheckResult.Degraded("No candle series are configured; the collector is idle", data: data),
            // Only the exception type is reported; messages can contain host names.
            PlanStatus.Failed => HealthCheckResult.Unhealthy($"Cannot read the candle series ({provider.LastError})", data: data),
            _ => HealthCheckResult.Degraded("Reading the candle series", data: data),
        });
    }
}

/// <summary>
/// Runs <paramref name="inner"/> only while <paramref name="isInUse"/> says the component is being used; otherwise it is simply
/// not in use and cannot be a problem. Because the worker opens its connections only once it knows what to collect, "never
/// connected" is judged from that moment (<paramref name="inUseSince"/>) instead of from process start.
/// </summary>
public sealed class GatedHealthCheck(
    IHealthCheck inner, Func<bool> isInUse, string notInUseDescription, Func<DateTime?> inUseSince, TimeSpan startupGrace, TimeProvider timeProvider) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!isInUse())
            return HealthCheckResult.Healthy(notInUseDescription);

        var result = await inner.CheckHealthAsync(context, cancellationToken);

        if (result.Status == HealthStatus.Unhealthy
            && inUseSince() is { } since
            && timeProvider.GetUtcNow().UtcDateTime - since <= startupGrace)
        {
            return HealthCheckResult.Degraded(result.Description, data: result.Data);
        }

        return result;
    }
}

/// <summary>A <see cref="GatedHealthCheck"/> that is in use while a Binance candle series of <paramref name="market"/> is being collected.</summary>
public sealed class MarketGatedHealthCheck(
    IHealthCheck inner, CandleSeriesProvider series, MarketTypeEnum market, TimeSpan startupGrace, TimeProvider timeProvider) : IHealthCheck
{
    private readonly GatedHealthCheck _gate = new(
        inner,
        () => series.Current.ForExchange(ExchangeEnum.Binance).Any(s => s.Series.MarketType == market),
        $"No {market} candle series configured",
        () => series.LoadedAt,
        startupGrace,
        timeProvider);

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        _gate.CheckHealthAsync(context, cancellationToken);
}
