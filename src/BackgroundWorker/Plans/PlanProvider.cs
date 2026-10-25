using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.Plans;

public enum PlanStatus
{
    /// <summary>The first read has not finished yet.</summary>
    Loading,

    /// <summary>The admin has chosen nothing (an empty table). The collector idles and keeps asking.</summary>
    WaitingForConfiguration,

    /// <summary>The plans could not be read (database down, ...). The collector keeps trying.</summary>
    Failed,

    /// <summary>The plans are known and the workers run on them.</summary>
    Loaded,
}

/// <summary>Where the plans a provider hands out come from (the admin's choice, stored in the config schema).</summary>
public interface IPlanSource<TPlan>
{
    /// <summary>The plans right now. Empty when the admin has chosen none. Throws when they cannot be read.</summary>
    Task<IReadOnlyList<TPlan>> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The one place the workers learn what to collect. It reads the plans from the database in the background and keeps trying
/// until there is at least one: an empty table means "nothing to collect" (the workers wait), and a database that is down is
/// not allowed to stop the process either. Once plans are known the list is fixed for the life of the process, so a change
/// made in the admin panel applies when the worker is restarted.
/// </summary>
/// <typeparam name="TPlan">What is being collected, e.g. a candle series or the funding rate and liquidations of a symbol.</typeparam>
public abstract class PlanProvider<TPlan>(IPlanSource<TPlan> source, TimeProvider timeProvider, ILogger logger, string what)
    : BackgroundService
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);

    private readonly TaskCompletionSource<IReadOnlyList<TPlan>> _loaded =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private volatile IReadOnlyList<TPlan> _current = [];
    private volatile PlanStatus _status = PlanStatus.Loading;
    private volatile string? _lastError;
    private long _loadedAtTicks;

    /// <summary>The plans being collected; empty until <see cref="Status"/> is <see cref="PlanStatus.Loaded"/>.</summary>
    public IReadOnlyList<TPlan> Current => _current;

    public PlanStatus Status => _status;

    /// <summary>The type of the exception of the last failed read (never its message: it can carry host names).</summary>
    public string? LastError => _lastError;

    /// <summary>When the plans became known, or null before that.</summary>
    public DateTime? LoadedAt => Interlocked.Read(ref _loadedAtTicks) is > 0 and var ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;

    /// <summary>Completes with the plans as soon as there is at least one. Cancelling the token (shutdown) cancels the wait.</summary>
    public Task<IReadOnlyList<TPlan>> WaitForPlansAsync(CancellationToken cancellationToken) =>
        _loaded.Task.WaitAsync(cancellationToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                var plans = await TryLoadAsync(stoppingToken);
                if (plans is { Count: > 0 })
                {
                    _current = plans;
                    Interlocked.Exchange(ref _loadedAtTicks, timeProvider.GetUtcNow().UtcDateTime.Ticks);
                    _status = PlanStatus.Loaded;
                    _lastError = null;
                    logger.LogInformation("Collecting {Count} {What}.", plans.Count, what);
                    _loaded.TrySetResult(plans);
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

    /// <returns>The plans, or null when they could not be read. Only shutdown throws.</returns>
    private async Task<IReadOnlyList<TPlan>?> TryLoadAsync(CancellationToken stoppingToken)
    {
        // A read that hangs (a database that accepts the connection and then says nothing) counts as a failure.
        using var timeout = new CancellationTokenSource(AttemptTimeout, timeProvider);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

        try
        {
            var plans = await source.LoadAsync(attempt.Token);
            if (plans.Count == 0)
            {
                if (_status != PlanStatus.WaitingForConfiguration)
                    logger.LogWarning("No {What} are configured; the collector is idle and checks again every {Delay}.", what, RetryDelay);

                _status = PlanStatus.WaitingForConfiguration;
                _lastError = null;
            }

            return plans;
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(stoppingToken);
        }
        catch (Exception ex)
        {
            // Includes the attempt timing out. Only the type is kept for health: messages can contain host names.
            if (_status != PlanStatus.Failed)
                logger.LogError(ex, "Cannot read the {What}; the collector waits and retries every {Delay}.", what, RetryDelay);

            // The error goes first so that whoever sees Failed (health, a test) never finds it without the error type.
            _lastError = ex.GetType().Name;
            _status = PlanStatus.Failed;
            return null;
        }
    }
}
