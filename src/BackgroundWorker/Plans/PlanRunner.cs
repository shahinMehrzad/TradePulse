using Microsoft.Extensions.Logging;

namespace BackgroundWorker.Plans;

/// <summary>
/// Keeps one worker's collection in step with what the admin chose, without restarting the process.
/// <para>
/// A worker states which part of the plans concerns it (<c>select</c>, e.g. only the symbols whose liquidations are wanted), may
/// split that part into independent groups (<c>keyOf</c>, e.g. one per time frame), and says how to collect one group
/// (<c>run</c>, which runs until cancelled). Whenever the plans change, the selection is made again and compared group by group:
/// a group that is the same is left running, so a change that does not concern it costs it nothing; a group whose items changed
/// is cancelled (it flushes and disconnects as it does at shutdown) and started again with the new items; a group that is gone is
/// stopped; a new one is started. An empty selection means nothing runs until there is one.
/// </para>
/// A collection that fails is not restarted: the exception ends the worker, as before, so the host stops visibly instead of the
/// collector carrying on half-working.
/// </summary>
public static class PlanRunner
{
    /// <summary>For a worker whose collection is one piece: everything it takes is a single group.</summary>
    public static Task RunAsync<TPlan, TItem>(
        PlanProvider<TPlan> provider,
        Func<IReadOnlyList<TPlan>, IReadOnlyList<TItem>> select,
        Func<IReadOnlyList<TItem>, CancellationToken, Task> run,
        ILogger logger,
        string what,
        CancellationToken stoppingToken)
        where TItem : notnull =>
        RunGroupedAsync(provider, select, _ => 0, (_, items, ct) => run(items, ct), logger, what, stoppingToken);

    public static async Task RunGroupedAsync<TPlan, TItem, TKey>(
        PlanProvider<TPlan> provider,
        Func<IReadOnlyList<TPlan>, IReadOnlyList<TItem>> select,
        Func<TItem, TKey> keyOf,
        Func<TKey, IReadOnlyList<TItem>, CancellationToken, Task> run,
        ILogger logger,
        string what,
        CancellationToken stoppingToken)
        where TItem : notnull
        where TKey : notnull
    {
        long version = 0;
        var started = false;                       // a collection was started before: starting one now is a restart
        IReadOnlyList<TItem>? previous = null;     // what the worker took at the last change
        var groups = new Dictionary<TKey, Group<TItem>>();
        Task<PlanSnapshot<TPlan>>? change = null;

        try
        {
            while (true)
            {
                change ??= provider.WaitForChangeAsync(version, stoppingToken);

                var waiting = new List<Task> { change };
                waiting.AddRange(groups.Values.Where(g => !g.Ended).Select(g => g.Running));
                var first = await Task.WhenAny(waiting);

                if (first != change)
                {
                    // A group's collection ended by itself (it had nothing to do). A failure surfaces here and ends the worker.
                    var ended = groups.Values.First(g => g.Running == first);
                    await ended.Running;
                    ended.Ended = true;
                    continue;
                }

                var snapshot = await change;
                change = null;
                version = snapshot.Version;

                // A change that leaves what this worker takes exactly as it was is no concern of it.
                var selected = select(snapshot.Plans);
                if (previous is not null && SameItems(previous, selected))
                    continue;

                previous = selected;
                var next = selected
                    .GroupBy(keyOf)
                    .ToDictionary(g => g.Key, g => (IReadOnlyList<TItem>)g.ToList());

                foreach (var key in groups.Keys.ToList())
                {
                    if (next.TryGetValue(key, out var items) && SameItems(groups[key].Items, items))
                        continue;

                    await StopAsync(groups[key]);
                    groups.Remove(key);
                }

                if (selected.Count == 0)
                    logger.LogInformation("No {What} to collect.", what);

                foreach (var (key, items) in next)
                {
                    if (groups.ContainsKey(key))
                        continue;

                    if (started)
                        logger.LogInformation("Collecting {Count} {What} for {Group} (the choice changed).", items.Count, what, key);

                    var cancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    groups[key] = new Group<TItem>(items, cancel, run(key, items, cancel.Token));
                    started = true;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Every group is told to stop first, then waited for, so they close their connections side by side.
            // (One that has already failed is the reason the worker is ending; its exception is on its way out.)
            var stopping = groups.Values.Where(g => !g.Running.IsFaulted).ToList();

            foreach (var group in stopping)
                await group.Cancel.CancelAsync();

            foreach (var group in stopping)
            {
                try
                {
                    await StopAsync(group);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One that fails while stopping must not keep the others from being stopped. If it is the one that ended the
                    // worker, that exception is already on its way out.
                    logger.LogError(ex, "A collection of the {What} failed while it was stopping.", what);
                }
            }
        }
    }

    /// <summary>Cancels one group's collection and waits until it has let go of its connections and queues.</summary>
    private static async Task StopAsync<TItem>(Group<TItem> group)
    {
        await group.Cancel.CancelAsync();
        try
        {
            await group.Running;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            group.Cancel.Dispose();
        }
    }

    private sealed class Group<TItem>(IReadOnlyList<TItem> items, CancellationTokenSource cancel, Task running)
    {
        public IReadOnlyList<TItem> Items { get; } = items;
        public CancellationTokenSource Cancel { get; } = cancel;
        public Task Running { get; } = running;

        /// <summary>The collection finished on its own; it is not waited for any more, but it is not started again for the same items.</summary>
        public bool Ended { get; set; }
    }

    private static bool SameItems<TItem>(IReadOnlyList<TItem> a, IReadOnlyList<TItem> b) where TItem : notnull =>
        a.Count == b.Count && new HashSet<TItem>(a).SetEquals(b);
}
