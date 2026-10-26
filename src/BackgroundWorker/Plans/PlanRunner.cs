using Microsoft.Extensions.Logging;

namespace BackgroundWorker.Plans;

/// <summary>
/// Keeps one worker's collection in step with what the admin chose, without restarting the process.
/// <para>
/// A worker states which part of the plans concerns it (<c>select</c>, e.g. only the symbols whose liquidations are wanted) and
/// how to collect that part (<c>run</c>, which runs until cancelled). Whenever the plans change, the selection is made again; if
/// it is the same, nothing happens (a change that does not concern this worker leaves it alone). If it differs, the running
/// collection is cancelled, which makes it flush and disconnect as it does at shutdown, and a new one starts with the new
/// selection. An empty selection means nothing runs until there is one.
/// </para>
/// A collection that fails is not restarted: the exception ends the worker, as before, so the host stops visibly instead of the
/// collector carrying on half-working.
/// </summary>
public static class PlanRunner
{
    public static async Task RunAsync<TPlan, TItem>(
        PlanProvider<TPlan> provider,
        Func<IReadOnlyList<TPlan>, IReadOnlyList<TItem>> select,
        Func<IReadOnlyList<TItem>, CancellationToken, Task> run,
        ILogger logger,
        string what,
        CancellationToken stoppingToken)
        where TItem : notnull
    {
        long version = 0;
        IReadOnlyList<TItem>? served = null;   // what the last collection was started for
        CancellationTokenSource? runCancel = null;
        Task? running = null;
        Task<PlanSnapshot<TPlan>>? change = null;

        try
        {
            while (true)
            {
                change ??= provider.WaitForChangeAsync(version, stoppingToken);
                var first = running is null ? await Task.WhenAny(change) : await Task.WhenAny(change, running);

                if (first == running)
                {
                    // The collection ended by itself (it had nothing to do). A failure surfaces here.
                    await running!;
                    runCancel!.Dispose();
                    runCancel = null;
                    running = null;
                    continue;
                }

                var snapshot = await change;
                change = null;
                version = snapshot.Version;

                var next = select(snapshot.Plans);
                if (served is not null && SameItems(served, next))
                    continue;

                await StopAsync();

                var restart = served is not null;
                served = next;

                if (next.Count == 0)
                {
                    logger.LogInformation("No {What} to collect.", what);
                    continue;
                }

                if (restart)
                    logger.LogInformation("Collecting {Count} {What} (the choice changed).", next.Count, what);

                runCancel = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                running = run(next, runCancel.Token);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await StopAsync();
        }

        // Cancels the running collection and waits until it has let go of its connections and queues.
        async Task StopAsync()
        {
            if (runCancel is null)
                return;

            await runCancel.CancelAsync();
            try
            {
                await running!;
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                runCancel.Dispose();
                runCancel = null;
                running = null;
            }
        }
    }

    private static bool SameItems<TItem>(IReadOnlyList<TItem> a, IReadOnlyList<TItem> b) where TItem : notnull =>
        a.Count == b.Count && new HashSet<TItem>(a).SetEquals(b);
}
