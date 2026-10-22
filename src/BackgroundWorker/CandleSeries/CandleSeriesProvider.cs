using Infrastructure.Configurations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.CandleSeries;

public enum CandleSeriesStatus
{
    /// <summary>The first read has not finished yet.</summary>
    Loading,

    /// <summary>The admin has chosen no series (an empty table). The collector idles and keeps asking.</summary>
    WaitingForConfiguration,

    /// <summary>The series could not be read (database down, ...). The collector keeps trying.</summary>
    Failed,

    /// <summary>The series are known and the workers run on them.</summary>
    Loaded,
}

/// <summary>
/// The one place the workers learn which candle series to collect. It reads them from the database in the background and
/// keeps trying until there is at least one: an empty table means "nothing to collect" (the workers wait), and a database
/// that is down is not allowed to stop the process either. Once series are known the list is fixed for the life of the
/// process, so a change made in the admin panel applies when the worker is restarted.
/// </summary>
public sealed class CandleSeriesProvider(ICandleSeriesSource source, TimeProvider timeProvider, ILogger<CandleSeriesProvider> logger)
    : BackgroundService
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);

    private readonly TaskCompletionSource<IReadOnlyList<BinanceSymbolConfigurations>> _loaded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile IReadOnlyList<BinanceSymbolConfigurations> _current = [];
    private volatile CandleSeriesStatus _status = CandleSeriesStatus.Loading;
    private volatile string? _lastError;
    private long _loadedAtTicks;

    /// <summary>The series being collected; empty until <see cref="Status"/> is <see cref="CandleSeriesStatus.Loaded"/>.</summary>
    public IReadOnlyList<BinanceSymbolConfigurations> Current => _current;

    public CandleSeriesStatus Status => _status;

    /// <summary>The type of the exception of the last failed read (never its message: it can carry host names).</summary>
    public string? LastError => _lastError;

    /// <summary>When the series became known, or null before that.</summary>
    public DateTime? LoadedAt => Interlocked.Read(ref _loadedAtTicks) is > 0 and var ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;

    /// <summary>Completes with the series as soon as there is at least one. Cancelling the token (shutdown) cancels the wait.</summary>
    public Task<IReadOnlyList<BinanceSymbolConfigurations>> WaitForSeriesAsync(CancellationToken cancellationToken) =>
        _loaded.Task.WaitAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                var series = await TryLoadAsync(stoppingToken);
                if (series is { Count: > 0 })
                {
                    _current = series;
                    Interlocked.Exchange(ref _loadedAtTicks, timeProvider.GetUtcNow().UtcDateTime.Ticks);
                    _status = CandleSeriesStatus.Loaded;
                    _lastError = null;
                    logger.LogInformation("Collecting {Count} candle series.", series.Count);
                    _loaded.TrySetResult(series);
                    return;
                }

                await Task.Delay(RetryDelay, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _loaded.TrySetCanceled(stoppingToken);
        }
    }

    /// <returns>The series, or null when they could not be read. Only shutdown throws.</returns>
    private async Task<IReadOnlyList<BinanceSymbolConfigurations>?> TryLoadAsync(CancellationToken stoppingToken)
    {
        // A read that hangs (a database that accepts the connection and then says nothing) counts as a failure.
        using var timeout = new CancellationTokenSource(AttemptTimeout, timeProvider);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

        try
        {
            var series = await source.LoadAsync(attempt.Token);
            if (series.Count == 0)
            {
                if (_status != CandleSeriesStatus.WaitingForConfiguration)
                    logger.LogWarning("No candle series are configured; the collector is idle and checks again every {Delay}.", RetryDelay);

                _status = CandleSeriesStatus.WaitingForConfiguration;
                _lastError = null;
            }

            return series;
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(stoppingToken);
        }
        catch (Exception ex)
        {
            // Includes the attempt timing out. Only the type is kept for health: messages can contain host names.
            if (_status != CandleSeriesStatus.Failed)
                logger.LogError(ex, "Cannot read the candle series; the collector waits and retries every {Delay}.", RetryDelay);

            _status = CandleSeriesStatus.Failed;
            _lastError = ex.GetType().Name;
            return null;
        }
    }
}
