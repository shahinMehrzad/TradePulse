using Domain.Services;
using BackgroundWorker.CandleSeries;
using Domain.Enums;
using Domain.Repositories;
using Infrastructure.Configurations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.Health;

/// <param name="LatestOpen">Open time (UTC) of the newest stored candle, null for an empty table.</param>
/// <param name="CandlesBehind">How many closed candles should already be stored but are not.</param>
/// <param name="Progressing">The newest candle moved forward since the previous observation (backfill or live sync at work).</param>
public sealed record TableFreshness(string Table, DateTime? LatestOpen, int CandlesBehind, bool Progressing);

/// <summary>
/// Measures how far the newest stored candle of every configured table lags behind the clock. A healthy connection
/// proves nothing when inserts fail, so this looks at the data itself. It is a singleton because it caches the
/// database answer (health checks are re-created on every run) and remembers the previous observation to see progress.
/// </summary>
public sealed class CandleFreshnessMonitor(
    IServiceScopeFactory scopeFactory, CandleSeriesProvider seriesProvider, TimeProvider timeProvider)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    /// <summary>Binance publishes a candle shortly after its boundary and the REST loop needs a moment to store it.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, DateTime?> _previous = [];
    private IReadOnlyList<TableFreshness>? _cached;
    private DateTime _cachedAt;

    public DateTime StartedAt { get; } = timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Throws on database errors; the check turns that into Unhealthy. A failed read is never cached.</summary>
    public async Task<IReadOnlyList<TableFreshness>> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (_cached is not null && now - _cachedAt < CacheDuration)
                return _cached;

            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<ICandleRepository>();

            var result = new List<TableFreshness>();
            foreach (var plan in seriesProvider.Current
                         .DistinctBy(p => p.Series))
            {
                var table = plan.Series.TableName;
                var interval = plan.Series.Interval;

                var latest = (await repository.GetLatestOpenDataTime(table, ct))?.UtcDateTime;
                var progressing = latest is not null
                    && _previous.TryGetValue(table, out var before)
                    && (before is null || latest > before);

                _previous[table] = latest;
                result.Add(new TableFreshness(table, latest, CandlesBehind(latest, interval, now), progressing));
            }

            // Nothing to measure yet (the series are not known) is not worth remembering: the answer changes the moment they are.
            if (result.Count > 0)
            {
                _cached = result;
                _cachedAt = now;
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Newest candle that must be stored by <paramref name="now"/> is the one before the current open candle
    /// (candles close on multiples of the interval since the epoch), allowing <see cref="Grace"/> after the boundary.
    /// </summary>
    public static int CandlesBehind(DateTime? latestOpen, TimeSpan interval, DateTime now)
    {
        if (latestOpen is null)
            return 0;

        var expectedOpen = new DateTime((now - Grace).Ticks / interval.Ticks * interval.Ticks - interval.Ticks, DateTimeKind.Utc);
        return latestOpen.Value >= expectedOpen ? 0 : (int)((expectedOpen - latestOpen.Value).Ticks / interval.Ticks);
    }
}

/// <summary>
/// A table that is current is healthy. One candle behind is degraded, <see cref="UnhealthyAfterCandles"/> or more is
/// unhealthy, unless the newest candle is still moving (initial backfill), which is reported healthy as catching up.
/// During <see cref="StartupGrace"/> after startup nothing is worse than degraded, because the first sync is still running.
/// </summary>
public sealed class CandleFreshnessHealthCheck(CandleFreshnessMonitor monitor, TimeProvider timeProvider) : IHealthCheck
{
    public const int UnhealthyAfterCandles = 3;
    private static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(10);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<TableFreshness> tables;
        try
        {
            tables = await monitor.GetAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Only the exception type is reported; messages can contain host names.
            return HealthCheckResult.Unhealthy($"Cannot read candle freshness ({ex.GetType().Name})");
        }

        // Which series exist is the candle-series check's business; with none there is simply nothing to measure.
        if (tables.Count == 0)
            return HealthCheckResult.Healthy("No candle series to check");

        var starting = timeProvider.GetUtcNow().UtcDateTime - monitor.StartedAt <= StartupGrace;
        var data = new Dictionary<string, object>();
        var status = HealthStatus.Healthy;
        var problems = new List<string>();

        foreach (var table in tables)
        {
            var (tableStatus, text) = Evaluate(table, starting);
            data[table.Table] = text;
            if (tableStatus != HealthStatus.Healthy)
                problems.Add($"{table.Table}: {text}");
            if (tableStatus < status)
                status = tableStatus;
        }

        var catchingUp = tables.Count(t => t.CandlesBehind > 0 && t.Progressing);
        if (problems.Count > 0)
            return new HealthCheckResult(status, string.Join("; ", problems), data: data);

        return HealthCheckResult.Healthy(
            catchingUp > 0 ? $"{tables.Count} tables, {catchingUp} still catching up" : $"{tables.Count} tables up to date", data);
    }

    private static (HealthStatus Status, string Text) Evaluate(TableFreshness table, bool starting)
    {
        if (table.LatestOpen is null)
            return (starting ? HealthStatus.Degraded : HealthStatus.Unhealthy, "no candles stored yet");

        if (table.CandlesBehind == 0)
            return (HealthStatus.Healthy, "up to date");

        var text = $"{table.CandlesBehind} candles behind";
        if (table.Progressing)
            return (HealthStatus.Healthy, text + " (catching up)");

        return (table.CandlesBehind >= UnhealthyAfterCandles && !starting ? HealthStatus.Unhealthy : HealthStatus.Degraded, text);
    }
}
