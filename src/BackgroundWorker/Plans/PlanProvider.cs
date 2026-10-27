using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.Plans;

public enum PlanStatus
{
    /// <summary>The first read has not finished yet.</summary>
    Loading,

    /// <summary>The admin has chosen nothing (an empty table). The collector idles and keeps asking.</summary>
    WaitingForConfiguration,

    /// <summary>The plans could not be read and none were ever known (database down at startup, ...). The collector keeps trying.</summary>
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

/// <summary>The plans as they were at one <see cref="Version"/>.</summary>
public sealed record PlanSnapshot<TPlan>(long Version, IReadOnlyList<TPlan> Plans);

/// <summary>
/// The one place the workers learn what to collect. It reads the plans from the database in the background and keeps reading:
/// every <see cref="RefreshInterval"/> it looks again, and when the list is different it publishes it under a new
/// <see cref="Version"/>, so a change made in the admin panel reaches the workers within a refresh (see <see cref="PlanRunner"/>).
/// <para>
/// A database that is down is not allowed to stop the process, nor to stop what is already being collected: before any list is known
/// the status is <see cref="PlanStatus.Failed"/> and the collector waits; afterwards the last known list stays in use and the failure
/// is only reported (<see cref="LastError"/>). An empty table is a list too: the admin removed everything, and the workers stop.
/// </para>
/// </summary>
/// <typeparam name="TPlan">What is being collected, e.g. a candle series or the funding rate and liquidations of a symbol.</typeparam>
public abstract class PlanProvider<TPlan>(IPlanSource<TPlan> source, TimeProvider timeProvider, ILogger logger, string what)
    : BackgroundService
{
    /// <summary>How long to wait before reading again after a failed read or while nothing is chosen.</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>How often a list that is known is read again, to see whether the admin changed it.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(15);

    private readonly Lock _gate = new();
    private TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IReadOnlyList<TPlan> _current = [];
    private long _version;

    private volatile PlanStatus _status = PlanStatus.Loading;
    private volatile string? _lastError;
    private long _loadedAtTicks;

    /// <summary>The plans being collected; empty until <see cref="Status"/> is <see cref="PlanStatus.Loaded"/>.</summary>
    public IReadOnlyList<TPlan> Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public PlanStatus Status => _status;

    /// <summary>
    /// The type of the exception of the last read, null when it succeeded. After a list is known this is not an outage: the last
    /// known list is still in use, but a change made since cannot be seen.
    /// </summary>
    public string? LastError => _lastError;

    /// <summary>0 until a list is known, then one more for every time the list turned out different (an empty one included).</summary>
    public long Version
    {
        get
        {
            lock (_gate)
                return _version;
        }
    }

    /// <summary>When the current list became known (the first read, or the read that found it different), or null before that.</summary>
    public DateTime? LoadedAt => Interlocked.Read(ref _loadedAtTicks) is > 0 and var ticks ? new DateTime(ticks, DateTimeKind.Utc) : null;

    /// <summary>Completes with the plans of the first version newer than <paramref name="afterVersion"/> (at once if there already is one).</summary>
    public async Task<PlanSnapshot<TPlan>> WaitForChangeAsync(long afterVersion, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                if (_version > afterVersion)
                    return new PlanSnapshot<TPlan>(_version, _current);

                changed = _changed.Task;
            }

            await changed.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Completes with the plans as soon as there is at least one. Cancelling the token (shutdown) cancels the wait.</summary>
    public async Task<IReadOnlyList<TPlan>> WaitForPlansAsync(CancellationToken cancellationToken)
    {
        long seen = 0;
        while (true)
        {
            var snapshot = await WaitForChangeAsync(seen, cancellationToken);
            if (snapshot.Plans.Count > 0)
                return snapshot.Plans;

            seen = snapshot.Version;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                var plans = await TryLoadAsync(stoppingToken);
                if (plans is not null)
                    Apply(plans);

                await Task.Delay(plans is { Count: > 0 } ? RefreshInterval : RetryDelay, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Nobody is left to tell them anything new: whoever still waits for a change is released.
            lock (_gate)
                _changed.TrySetCanceled(stoppingToken);
        }
    }

    /// <summary>Takes a successful read: publishes the list if it is a different one, and moves the status.</summary>
    private void Apply(IReadOnlyList<TPlan> plans)
    {
        _lastError = null;

        TaskCompletionSource? signal = null;
        bool first;
        IReadOnlyList<TPlan> previous;
        lock (_gate)
        {
            first = _version == 0;
            previous = _current;
            if (first || !SameSet(_current, plans))
            {
                // The list and the time are in place before the status says so.
                _current = plans;
                Interlocked.Exchange(ref _loadedAtTicks, timeProvider.GetUtcNow().UtcDateTime.Ticks);
                _version++;
                signal = _changed;
                _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        var status = plans.Count > 0 ? PlanStatus.Loaded : PlanStatus.WaitingForConfiguration;
        if (status == PlanStatus.WaitingForConfiguration && _status != PlanStatus.WaitingForConfiguration)
            logger.LogWarning("No {What} are configured; the collector is idle and checks again every {Delay}.", what, RetryDelay);

        _status = status;

        if (signal is not null)
        {
            if (plans.Count > 0 && first)
                logger.LogInformation("Collecting {Count} {What}.", plans.Count, what);
            else if (!first)
            {
                // A row that was edited (not added or removed) is one of each, and the total can stay the same.
                logger.LogInformation("The {What} changed: {Added} new, {Removed} gone (an edited one counts as both), {Count} now.",
                    what, plans.Except(previous).Count(), previous.Except(plans).Count(), plans.Count);
            }

            signal.TrySetResult();
        }
    }

    private static bool SameSet(IReadOnlyList<TPlan> a, IReadOnlyList<TPlan> b) =>
        a.Count == b.Count && new HashSet<TPlan>(a).SetEquals(b);

    /// <returns>The plans, or null when they could not be read. Only shutdown throws.</returns>
    private async Task<IReadOnlyList<TPlan>?> TryLoadAsync(CancellationToken stoppingToken)
    {
        // A read that hangs (a database that accepts the connection and then says nothing) counts as a failure.
        using var timeout = new CancellationTokenSource(AttemptTimeout, timeProvider);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeout.Token);

        try
        {
            return await source.LoadAsync(attempt.Token);
        }
        catch (Exception) when (stoppingToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(stoppingToken);
        }
        catch (Exception ex)
        {
            // Includes the attempt timing out. Only the type is kept for health: messages can contain host names.
            var wasFailing = _lastError is not null;
            var known = Version > 0;

            // The error goes first so that whoever sees Failed (health, a test) never finds it without the error type.
            _lastError = ex.GetType().Name;

            if (!known)
            {
                if (_status != PlanStatus.Failed)
                    logger.LogError(ex, "Cannot read the {What}; the collector waits and retries every {Delay}.", what, RetryDelay);

                _status = PlanStatus.Failed;
            }
            else if (!wasFailing)
            {
                logger.LogWarning(ex, "Cannot read the {What}; the last known list stays in use and a change made now cannot be seen. Trying again every {Delay}.",
                    what, RefreshInterval);
            }

            return null;
        }
    }
}
