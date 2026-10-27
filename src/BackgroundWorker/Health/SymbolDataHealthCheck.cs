using BackgroundWorker.Plans;
using BackgroundWorker.SymbolData;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BackgroundWorker.Health;

/// <summary>
/// Whether the worker knows which symbols collect their funding rate or liquidations. Unlike the candle series, an empty table
/// is a normal choice (candles only), so it is healthy; only a database that cannot be read is unhealthy, because then the
/// funding rate and liquidation workers wait forever without anyone seeing why.
/// </summary>
public sealed class SymbolDataHealthCheck(SymbolDataProvider provider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object> { ["status"] = provider.Status.ToString(), ["symbols"] = provider.Current.Count };

        return Task.FromResult(provider.Status switch
        {
            PlanStatus.Loaded when provider.LastError is { } error => HealthCheckResult.Degraded(
                $"Collecting symbol data for {provider.Current.Count} symbols, but the list cannot be refreshed ({error}); changes made now are not seen", data: data),
            PlanStatus.Loaded => HealthCheckResult.Healthy($"Collecting symbol data for {provider.Current.Count} symbols", data),
            PlanStatus.WaitingForConfiguration => HealthCheckResult.Healthy("No funding rate or liquidation collection is configured", data),
            // Only the exception type is reported; messages can contain host names.
            PlanStatus.Failed => HealthCheckResult.Unhealthy($"Cannot read the symbol data configurations ({provider.LastError})", data: data),
            _ => HealthCheckResult.Degraded("Reading the symbol data configurations", data: data),
        });
    }
}
