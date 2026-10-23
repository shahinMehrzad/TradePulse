using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BackgroundWorker.Health;

/// <summary>
/// Health of a request/response API (REST), judged by consecutive failures: one failure is a blip (degraded),
/// several in a row mean the connection is really down (unhealthy). A component that has not made a request yet
/// is healthy, because silence is expected for slow schedules such as a daily candle.
/// </summary>
public sealed class RequestHealthCheck(
    string component, WorkerHealthState state, TimeProvider timeProvider, int unhealthyAfterFailures = 3) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = state.Get(component);
        var data = new Dictionary<string, object>
        {
            ["consecutiveFailures"] = snapshot.ConsecutiveFailures,
            ["lastSuccessAt"] = snapshot.LastSuccessAt?.ToString("O") ?? "never",
        };
        if (snapshot.LastError is not null)
            data["lastError"] = snapshot.LastError;

        if (snapshot.ConsecutiveFailures == 0)
        {
            var description = snapshot.LastSuccessAt is { } last
                ? $"Last request succeeded {Ago(timeProvider, last)}"
                : "No requests made yet";
            return Task.FromResult(HealthCheckResult.Healthy(description, data));
        }

        var message = $"{snapshot.ConsecutiveFailures} consecutive request failures";
        return Task.FromResult(snapshot.ConsecutiveFailures >= unhealthyAfterFailures
            ? HealthCheckResult.Unhealthy(message, data: data)
            : HealthCheckResult.Degraded(message, data: data));
    }

    internal static string Ago(TimeProvider timeProvider, DateTime utc) =>
        $"{(timeProvider.GetUtcNow().UtcDateTime - utc).TotalSeconds:F0}s ago";
}

/// <param name="StartupGrace">How long "not connected yet" is tolerated after startup.</param>
/// <param name="UnhealthyAfterDisconnected">A lost connection is degraded (the client reconnects by itself) until this long.</param>
/// <param name="MaxSilence">Connected but no message for this long. Null disables the check (use for rare-event streams).</param>
/// <param name="SilenceStatus">Status to report when <paramref name="MaxSilence"/> is exceeded.</param>
public sealed record WebSocketHealthOptions(
    TimeSpan StartupGrace,
    TimeSpan UnhealthyAfterDisconnected,
    TimeSpan? MaxSilence,
    HealthStatus SilenceStatus);

/// <summary>Health of a WebSocket subscription from its connection state and, optionally, message flow.</summary>
public sealed class WebSocketHealthCheck(
    string component, WebSocketHealthOptions options, WorkerHealthState state, TimeProvider timeProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var snapshot = state.Get(component);
        var data = new Dictionary<string, object>
        {
            ["connected"] = snapshot.Connected?.ToString() ?? "unknown",
            ["messages"] = snapshot.MessageCount,
            ["lastMessageAt"] = snapshot.LastMessageAt?.ToString("O") ?? "never",
        };

        if (snapshot.Connected is null)
        {
            return Task.FromResult(now - state.StartedAt <= options.StartupGrace
                ? HealthCheckResult.Degraded("Connecting", data: data)
                : HealthCheckResult.Unhealthy("Never connected", data: data));
        }

        if (snapshot.Connected is false)
        {
            var downFor = now - (snapshot.ConnectionChangedAt ?? state.StartedAt);
            data["disconnectedForSeconds"] = (int)downFor.TotalSeconds;
            return Task.FromResult(downFor > options.UnhealthyAfterDisconnected
                ? HealthCheckResult.Unhealthy($"Disconnected for {downFor.TotalSeconds:F0}s", data: data)
                : HealthCheckResult.Degraded($"Disconnected for {downFor.TotalSeconds:F0}s, reconnecting", data: data));
        }

        if (options.MaxSilence is { } maxSilence)
        {
            var silentFor = now - (snapshot.LastMessageAt ?? snapshot.ConnectionChangedAt ?? state.StartedAt);
            if (silentFor > maxSilence)
            {
                return Task.FromResult(new HealthCheckResult(options.SilenceStatus,
                    $"Connected but no message for {silentFor.TotalMinutes:F0} min", data: data));
            }
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            snapshot.LastMessageAt is { } last ? $"Connected, last message {RequestHealthCheck.Ago(timeProvider, last)}" : "Connected", data));
    }
}
