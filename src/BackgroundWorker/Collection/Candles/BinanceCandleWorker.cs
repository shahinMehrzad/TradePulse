using Application.Exchange;
using Application.Notifications;
using BackgroundWorker.Alerts;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Health;
using BackgroundWorker.Plans;
using BackgroundWorker.Scheduling;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Threading.Channels;

namespace BackgroundWorker.Collection.Candles;

public class BinanceCandleWorker(IServiceScopeFactory scopeFactory,
        ILogger<BinanceCandleWorker> logger,
        CandleSeriesProvider seriesProvider,
        ICandleGateway candleGateway,
        CandleSyncCoordinator coordinator,
        WorkerHealthState health,
        OpenGaps openGaps,
        GapRepairSchedule schedule,
        IAlertSink alerts,
        CollectionStats stats) : BackgroundService
{
    private const int MaxParallelSymbols = 4;
    private const int MaxRetries = 5;

    private static readonly TimeSpan TableRetryDelay = TimeSpan.FromSeconds(10);

    // The wake-up for the short repair of a long time frame comes a minute after the full hour, well away from the candle boundary (3 s after it).
    private static readonly TimeSpan RecentRepairOffset = TimeSpan.FromMinutes(1);

    private enum Wake { Boundary, Resync, RecentRepair }

    /// <summary>How long to wait after the n-th failed request of a range (a test sets it to nothing).</summary>
    internal Func<int, TimeSpan> Backoff { get; init; } = static retry => TimeSpan.FromSeconds(Math.Pow(2, retry));

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        PlanRunner.RunGroupedAsync(seriesProvider,
            plans => plans.ForExchange(ExchangeEnum.Binance).Select(p => p with { CollectOpenInterest = false, CollectMarketStatistics = false }).ToList(),
            plan => (int)plan.Series.Timeframe,
            CollectAsync, logger, "candle series", stoppingToken);

    private async Task CollectAsync(int timeframeSeconds, IReadOnlyList<CandleSeriesPlan> series, CancellationToken ct)
    {
        try
        {
            await EnsureTablesAsync(series, ct);
            await RunIntervalLoopAsync(TimeSpan.FromSeconds(timeframeSeconds), series.ToList(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }

    private async Task EnsureTablesAsync(IReadOnlyList<CandleSeriesPlan> series, CancellationToken ct)
    {
        foreach (var plan in series)
        {
            var tableName = plan.Series.TableName;

            while (true)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<ICandleRepository>().EnsureTableExistsAsync(tableName, ct);
                    break;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    logger.LogError(ex, "Cannot create table {Table}; retrying in {Delay}", tableName, TableRetryDelay);
                    await Task.Delay(TableRetryDelay, ct);
                }
            }
        }
    }

    private async Task RunIntervalLoopAsync(TimeSpan interval, List<CandleSeriesPlan> plans, CancellationToken ct)
    {
        // Registered first so that a resync during the first pass is not lost; unregistered when the loop is replaced.
        var resyncRequests = coordinator.Register();

        try
        {
            await SyncAllSymbolsAsync(plans, ct);
            await RepairGapsAsync(plans, force: false, ct);

            while (!ct.IsCancellationRequested)
            {
                // The boundary sync always runs, even when the live stream delivered the candle (the watchdog):
                // it is one cheap request per symbol and ON CONFLICT DO NOTHING makes the overlap harmless.
                var wake = await WaitForBoundaryOrResyncAsync(interval, resyncRequests, ct);
                if (wake != Wake.RecentRepair)
                    await SyncAllSymbolsAsync(plans, ct);

                // After a reconnect everything is repaired, otherwise only the tables whose daily turn has come.
                await RepairGapsAsync(plans, force: wake == Wake.Resync, ct);

                // Every wake-up asks whose hourly turn has come: a cheap look in memory, and a look at the newest candles only for those.
                await RepairRecentGapsAsync(plans, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            coordinator.Unregister(resyncRequests);
        }
    }

    /// <summary>
    /// Waits for the next candle boundary, a resync request, or (only for a time frame longer than the period of the short repair, whose boundaries
    /// come too seldom for it) the next full hour.
    /// </summary>
    private async Task<Wake> WaitForBoundaryOrResyncAsync(TimeSpan interval, ChannelReader<bool> resyncRequests, CancellationToken ct)
    {
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var boundary = Task.Delay(MarketClock.GetDelayUntilNextBoundary(interval, MarketClock.CloseOffset, DateTime.UtcNow), stopWaiting.Token);
        var request = resyncRequests.WaitToReadAsync(stopWaiting.Token).AsTask();
        var recent = interval > schedule.RecentPeriod
            ? Task.Delay(MarketClock.GetDelayUntilNextBoundary(schedule.RecentPeriod, RecentRepairOffset, DateTime.UtcNow), stopWaiting.Token)
            : Task.Delay(Timeout.InfiniteTimeSpan, stopWaiting.Token);

        var first = await Task.WhenAny(boundary, request, recent);
        await stopWaiting.CancelAsync();
        ct.ThrowIfCancellationRequested();

        // The losers were cancelled just above; observe them so they do not surface as unobserved exceptions.
        _ = Task.WhenAll(boundary, request, recent).ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        if (first == request && request.IsCompletedSuccessfully)
        {
            while (resyncRequests.TryRead(out _)) { }
            return Wake.Resync;
        }

        return first == recent ? Wake.RecentRepair : Wake.Boundary;
    }

    private async Task SyncAllSymbolsAsync(List<CandleSeriesPlan> plans, CancellationToken ct)
    {
        await Parallel.ForEachAsync(
            plans,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSymbols, CancellationToken = ct },
            async (plan, token) =>
            {
                try
                {
                    await SyncSymbolAsync(plan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Sync failed for {Symbol}", plan.Series.Symbol);
                }
            });
    }

    private async Task SyncSymbolAsync(CandleSeriesPlan plan, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICandleRepository>();

        var tableName = plan.Series.TableName;
        var intervalSpan = plan.Series.Interval;

        // A failure here must surface: treating it as "table is empty" would re-download the whole history.
        var lastOpen = await repository.GetLatestOpenDataTime(tableName, ct);
        var from = lastOpen is null ? plan.StartDate : lastOpen.Value.UtcDateTime + intervalSpan;

        var fetch = await FetchRangeAsync(plan, repository, tableName, from, to: null, ct);

        // Only a complete pass makes the table safe for live candles (see CandleSyncCoordinator).
        if (fetch.Completed)
            coordinator.MarkReady(tableName);
    }

    private async Task RepairGapsAsync(List<CandleSeriesPlan> plans, bool force, CancellationToken ct)
    {
        // The gap query scans a whole table: only the tables that are due, unless everything is asked for.
        var due = force ? plans : plans.Where(p => schedule.IsDue(p.Series.TableName, p.StartDate)).ToList();
        if (due.Count == 0)
            return;

        await Parallel.ForEachAsync(
            due,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSymbols, CancellationToken = ct },
            async (plan, token) =>
            {
                try
                {
                    await RepairSymbolGapsAsync(plan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Gap repair failed for {Symbol}", plan.Series.Symbol);
                }
            });
    }

    /// <summary>
    /// The short repair: the holes among the newest candles of the tables whose hourly turn has come. It only fills. What the panel and the alerts say
    /// about the holes of a table stays the daily full scan's: a look at the last days only must not wipe out what the whole table showed.
    /// </summary>
    internal async Task RepairRecentGapsAsync(List<CandleSeriesPlan> plans, CancellationToken ct)
    {
        var due = plans.Where(p => schedule.IsRecentDue(p.Series.TableName)).ToList();
        if (due.Count == 0)
            return;

        await Parallel.ForEachAsync(
            due,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSymbols, CancellationToken = ct },
            async (plan, token) =>
            {
                try
                {
                    await RepairRecentSymbolGapsAsync(plan, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Short gap repair failed for {Symbol}", plan.Series.Symbol);
                }
            });
    }

    private async Task RepairRecentSymbolGapsAsync(CandleSeriesPlan plan, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICandleRepository>();

        var tableName = plan.Series.TableName;

        // A failure here must surface, like in the full scan, and the table is not marked: it is tried again at the next wake-up.
        var gaps = await repository.GetRecentGapsAsync(tableName, plan.Series.Interval, schedule.RecentSince(plan.StartDate), ct);

        foreach (var gap in gaps)
        {
            if (!schedule.MayAsk(tableName, gap.FirstMissingOpenTime, gap.LastMissingOpenTime))
                continue;

            var (inserted, completed) = await FetchRangeAsync(plan, repository, tableName, gap.FirstMissingOpenTime, gap.LastMissingOpenTime, ct);

            if (inserted > 0)
                logger.LogWarning("Repaired a recent gap {From:u} .. {To:u} in {Table}: {Count} candles",
                    gap.FirstMissingOpenTime, gap.LastMissingOpenTime, tableName, inserted);

            // Only an answer counts. A request that failed says nothing about whether the exchange has the candles.
            if (!completed)
                continue;

            schedule.RecordAsk(tableName, gap.FirstMissingOpenTime, gap.LastMissingOpenTime);
            if (inserted == 0)
                logger.LogInformation("Recent gap {From:u} .. {To:u} in {Table} was not filled (the exchange had nothing, asked {Asked} of {Max} times)",
                    gap.FirstMissingOpenTime, gap.LastMissingOpenTime, tableName,
                    schedule.AsksAbout(tableName, gap.FirstMissingOpenTime, gap.LastMissingOpenTime), schedule.MaxAsksPerHole);
        }

        schedule.ForgetAsksExcept(tableName, gaps.Select(g => (g.FirstMissingOpenTime, g.LastMissingOpenTime)).ToList());
        schedule.MarkRecentRepaired(tableName);
    }

    private async Task RepairSymbolGapsAsync(CandleSeriesPlan plan, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICandleRepository>();

        var tableName = plan.Series.TableName;
        var intervalSpan = plan.Series.Interval;

        var gaps = await repository.GetGapsAsync(tableName, intervalSpan, plan.StartDate, ct);
        var filledSome = false;
        foreach (var gap in gaps)
        {
            if (gap.BeforeFirstCandle)
            {
                filledSome |= await TryFillBeforeFirstCandleAsync(plan, repository, gap, ct);
                continue;
            }

            var (inserted, _) = await FetchRangeAsync(plan, repository, tableName,
                gap.FirstMissingOpenTime, gap.LastMissingOpenTime, ct);

            // Binance itself has no candle for some periods (maintenance, no trades); those stay open and are retried next run.
            if (inserted == 0)
                logger.LogInformation("Gap {From:u} .. {To:u} in {Table} could not be filled (likely missing on exchange)",
                    gap.FirstMissingOpenTime, gap.LastMissingOpenTime, tableName);
            else
            {
                filledSome = true;
                logger.LogWarning("Repaired gap {From:u} .. {To:u} in {Table}: {Count} candles",
                    gap.FirstMissingOpenTime, gap.LastMissingOpenTime, tableName, inserted);
            }
        }

        // A hole filled in part leaves a smaller one behind, so after any insert the table is looked at again.
        var open = filledSome ? await repository.GetGapsAsync(tableName, intervalSpan, plan.StartDate, ct) : gaps;

        // The run in front of the first candle is where the series starts, not a hole in it.
        var beforeFirst = open.Where(g => g.BeforeFirstCandle).Select(g => (DateTime?)(g.LastMissingOpenTime + intervalSpan)).FirstOrDefault();
        var holes = open.Where(g => !g.BeforeFirstCandle).ToList();
        var previous = openGaps.Set(tableName, holes, intervalSpan, beforeFirst);

        // Not at the first look (the old holes of a new series are not news): only when a later look finds more.
        if (previous is { } before && holes.Count > before.Gaps && openGaps.Get(tableName) is { } now)
            alerts.Raise(new Alert($"New gap in {tableName}",
                $"{now.Gaps} gap(s), {now.MissingCandles} candle(s) missing, from {now.Oldest:yyyy-MM-dd HH:mm} UTC. The exchange has no candles for them."));

        schedule.MarkRepaired(tableName, plan.StartDate);

        // The full scan has looked at the newest candles too.
        schedule.MarkRecentRepaired(tableName);
    }

    // Usually the symbol was listed after the start date and the exchange has nothing there: ask once, then not again while the
    // first candle stays the same. True when something was inserted.
    private async Task<bool> TryFillBeforeFirstCandleAsync(CandleSeriesPlan plan, ICandleRepository repository, CandleGap gap, CancellationToken ct)
    {
        var tableName = plan.Series.TableName;
        var firstCandle = gap.LastMissingOpenTime + plan.Series.Interval;

        if (schedule.KnowsNothingBefore(tableName, firstCandle))
            return false;

        var (inserted, _) = await FetchRangeAsync(plan, repository, tableName, gap.FirstMissingOpenTime, gap.LastMissingOpenTime, ct);
        if (inserted == 0)
        {
            schedule.RememberNothingBefore(tableName, firstCandle);
            logger.LogDebug("{Table} starts at {First:u}: the exchange has no candles between the start date {Start:u} and then",
                tableName, firstCandle, plan.StartDate);
            return false;
        }

        logger.LogWarning("Repaired the start of {Table} ({From:u} .. {To:u}): {Count} candles",
            tableName, gap.FirstMissingOpenTime, gap.LastMissingOpenTime, inserted);
        return true;
    }

    private async Task<(int Inserted, bool Completed)> FetchRangeAsync(
        CandleSeriesPlan plan, ICandleRepository repository, string tableName,
        DateTime from, DateTime? to, CancellationToken ct)
    {
        var intervalSpan = plan.Series.Interval;
        var retry = 0;
        var totalInserted = 0;

        while (!ct.IsCancellationRequested)
        {
            if (from + intervalSpan > DateTime.UtcNow || (to is not null && from > to))
                return (totalInserted, true);

            var result = await candleGateway.GetCandlesAsync(plan.Series, from, to, ct);

            if (!result.Success)
            {
                health.RecordFailure(HealthComponents.CandleRequests, result.Error);
                logger.LogWarning("Kline request failed for {Symbol}: {Error}", plan.Series.Symbol, result.Error);
                if (++retry > MaxRetries)
                    return (totalInserted, false);
                await Task.Delay(Backoff(retry), ct); // exponential backoff
                continue;
            }

            health.RecordSuccess(HealthComponents.CandleRequests);
            retry = 0;
            var page = result.Value;
            if (page.Items.Count == 0)
                return (totalInserted, true);

            // Only closed candles are stored. Usually only the very last one of the newest page is still open, so the common
            // case (everything closed) is checked once and allocates nothing.
            var now = DateTime.UtcNow;
            var closed = page.Items[^1].TimeRange.CloseTime < now
                ? page.Items
                : page.Items.Where(c => c.TimeRange.CloseTime < now).ToList();
            if (closed.Count == 0)
                return (totalInserted, true);

            var inserted = await repository.BulkInsert(tableName, closed, ct);
            stats.Stored(MarketDataTypeEnum.Candle, inserted);
            totalInserted += inserted;
            logger.LogDebug("Inserted {Count} candles for {Symbol}", closed.Count, plan.Series.Symbol);

            from = closed[^1].TimeRange.OpenTime + intervalSpan;

            if (!page.HasMore)
                return (totalInserted, true);
        }

        return (totalInserted, false); // only reached when cancelled
    }
}
