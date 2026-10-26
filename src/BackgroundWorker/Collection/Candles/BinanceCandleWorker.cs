using Application.Exchange;
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
        CollectionStats stats) : BackgroundService
{
    private const int MaxParallelSymbols = 4;
    private const int MaxRetries = 5;

    private static readonly TimeSpan GapCheckPeriod = TimeSpan.FromHours(24);
    private static readonly TimeSpan TableRetryDelay = TimeSpan.FromSeconds(10);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        PlanRunner.RunAsync(seriesProvider, plans => plans.ForExchange(ExchangeEnum.Binance).ToList(), CollectAsync, logger, "candle series", stoppingToken);

    private async Task CollectAsync(IReadOnlyList<CandleSeriesPlan> series, CancellationToken ct)
    {
        try
        {
            await EnsureTablesAsync(series, ct);

            var groups = series
                .GroupBy(p => (int)p.Series.Timeframe)
                .ToList();

            await Task.WhenAll(groups.Select(g =>
                RunIntervalLoopAsync(TimeSpan.FromSeconds(g.Key), g.ToList(), ct)));
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
        var resyncRequests = coordinator.Register();

        try
        {
            await SyncAllSymbolsAsync(plans, ct);
            await RepairGapsAsync(plans, ct);
            var lastGapCheck = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                var resync = await WaitForBoundaryOrResyncAsync(interval, resyncRequests, ct);
                await SyncAllSymbolsAsync(plans, ct);

                if (resync)
                {
                    await RepairGapsAsync(plans, ct);
                    lastGapCheck = DateTime.UtcNow;
                }
                else if (DateTime.UtcNow - lastGapCheck >= GapCheckPeriod)
                {
                    await RepairGapsAsync(plans, ct);
                    lastGapCheck = DateTime.UtcNow;
                }
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

    private static async Task<bool> WaitForBoundaryOrResyncAsync(TimeSpan interval, ChannelReader<bool> resyncRequests, CancellationToken ct)
    {
        using var stopWaiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var boundary = Task.Delay(MarketClock.GetDelayUntilNextBoundary(interval, MarketClock.CloseOffset, DateTime.UtcNow), stopWaiting.Token);
        var request = resyncRequests.WaitToReadAsync(stopWaiting.Token).AsTask();

        var first = await Task.WhenAny(boundary, request);
        await stopWaiting.CancelAsync();
        ct.ThrowIfCancellationRequested();

        // The loser was cancelled just above; observe it so it does not surface as an unobserved exception.
        _ = Task.WhenAll(boundary, request).ContinueWith(static t => _ = t.Exception, TaskScheduler.Default);

        if (first != request || !request.IsCompletedSuccessfully)
            return false;

        while (resyncRequests.TryRead(out _)) { }
        return true;
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

    private async Task RepairGapsAsync(List<CandleSeriesPlan> plans, CancellationToken ct)
    {
        await Parallel.ForEachAsync(
            plans,
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

        // What is still open: the holes nothing could be inserted for. A hole that was filled in part leaves a smaller one behind,
        // so when anything was inserted the table is looked at again instead of guessing.
        var open = filledSome ? await repository.GetGapsAsync(tableName, intervalSpan, plan.StartDate, ct) : gaps;
        openGaps.Set(tableName, open, intervalSpan);
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
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)), ct); // exponential backoff
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
