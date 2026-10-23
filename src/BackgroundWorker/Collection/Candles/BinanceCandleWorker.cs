using Domain.ValueObjects;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Exchange.Binance;
using BackgroundWorker.Health;
using BackgroundWorker.Scheduling;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.SharedApis;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Infrastructure.Configurations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Channels;

namespace BackgroundWorker.Collection.Candles;

public class BinanceCandleWorker(IServiceScopeFactory scopeFactory,
        ILogger<BinanceCandleWorker> logger,
        CandleSeriesProvider seriesProvider,
        IBinanceRestClient binanceRestClient,
        IBinanceSocketClient binanceSocketClient,
        BinanceRateLimiters rateLimiters,
        CandleSyncCoordinator coordinator,
        WorkerHealthState health) : BackgroundService
{
    private readonly IBinanceRestClient _binanceClient = binanceRestClient;
    private readonly IBinanceSocketClient _binanceSocketClient = binanceSocketClient;
    private const int MaxKlineLimit = 1000;
    private const int MaxParallelSymbols = 4;
    private const int MaxRetries = 5;

    private static readonly TimeSpan GapCheckPeriod = TimeSpan.FromHours(24);
    private static readonly TimeSpan TableRetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The admin chooses the series; with none chosen (or the database unreachable) there is nothing to do but wait.
            var series = (await seriesProvider.WaitForSeriesAsync(stoppingToken)).ForExchange(ExchangeEnum.Binance).ToList();
            await EnsureTablesAsync(series, stoppingToken);

            var groups = series
                .GroupBy(p => (int)p.Series.Timeframe)
                .ToList();

            await Task.WhenAll(groups.Select(g =>
                RunIntervalLoopAsync(TimeSpan.FromSeconds(g.Key), g.ToList(), stoppingToken)));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Creates the table of every series that does not have one yet (idempotent). A database error is retried, not
    /// thrown: an exception out of a worker would stop the whole host.
    /// </summary>
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
        try
        {
            await SyncAllSymbolsAsync(plans, ct);
            await RepairGapsAsync(plans, ct);
            var lastGapCheck = DateTime.UtcNow;
            var resyncRequests = coordinator.Register();

            while (!ct.IsCancellationRequested)
            {
                // The boundary sync always runs, even when the live stream delivered the candle (the watchdog):
                // it is one cheap request per symbol and ON CONFLICT DO NOTHING makes the overlap harmless.
                var resync = await WaitForBoundaryOrResyncAsync(interval, resyncRequests, ct);
                await SyncAllSymbolsAsync(plans, ct);

                if (resync)
                {
                    // The live stream was down for a while; fill what it missed, including holes before newer live candles.
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
    }

    /// <summary>Waits for the next candle boundary or a resync request, whichever comes first. True when resync woke it.</summary>
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
        foreach (var gap in gaps)
        {
            var (inserted, _) = await FetchRangeAsync(plan, repository, tableName,
                gap.FirstMissingOpenTime, gap.LastMissingOpenTime, ct);

            // Binance itself has no candle for some periods (maintenance, no trades); those stay open and are retried next run.
            if (inserted == 0)
                logger.LogInformation("Gap {From:u} .. {To:u} in {Table} could not be filled (likely missing on exchange)",
                    gap.FirstMissingOpenTime, gap.LastMissingOpenTime, tableName);
            else
                logger.LogWarning("Repaired gap {From:u} .. {To:u} in {Table}: {Count} candles",
                    gap.FirstMissingOpenTime, gap.LastMissingOpenTime, tableName, inserted);
        }
    }

    /// <summary>
    /// Downloads closed candles starting at <paramref name="from"/> (and up to <paramref name="to"/> when given,
    /// inclusive open time) and stores them. Returns the number of newly inserted rows and whether the range was
    /// fetched to its end (false when the request kept failing and the work was given up).
    /// </summary>
    private async Task<(int Inserted, bool Completed)> FetchRangeAsync(
        CandleSeriesPlan plan, ICandleRepository repository, string tableName,
        DateTime from, DateTime? to, CancellationToken ct)
    {
        var intervalSpan = plan.Series.Interval;
        var limiter = rateLimiters.For(plan.Series.MarketType);
        var weight = GetKlineWeight(plan.Series.MarketType, MaxKlineLimit);
        var retry = 0;
        var totalInserted = 0;

        while (!ct.IsCancellationRequested)
        {
            if (from + intervalSpan > DateTime.UtcNow || (to is not null && from > to))
                return (totalInserted, true);

            await limiter.AcquireAsync(weight, ct);

            // Binance treats endTime as inclusive on open time.
            var result = plan.Series.MarketType == MarketTypeEnum.Futures
                ? await _binanceClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    plan.Series.Symbol.ToString(), (KlineInterval)plan.Series.Timeframe,
                    startTime: from, endTime: to, limit: MaxKlineLimit, ct: ct)
                : await _binanceClient.SpotApi.ExchangeData.GetKlinesAsync(
                    plan.Series.Symbol.ToString(), (KlineInterval)plan.Series.Timeframe,
                    startTime: from, endTime: to, limit: MaxKlineLimit, ct: ct);

            if (!result.Success)
            {
                health.RecordFailure(HealthComponents.CandleRequests, result.Error?.Message);
                logger.LogWarning("Kline request failed for {Symbol}: {Error}", plan.Series.Symbol, result.Error);
                if (++retry > MaxRetries)
                    return (totalInserted, false);
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)), ct); // exponential backoff
                continue;
            }

            health.RecordSuccess(HealthComponents.CandleRequests);
            retry = 0;
            var data = result.Data.ToList();
            if (data.Count == 0)
                return (totalInserted, true);

            var now = DateTime.UtcNow;
            var closed = data.Where(k => k.CloseTime < now).ToList();
            if (closed.Count == 0)
                return (totalInserted, true);

            var candles = closed.Select(k => new Candle(
                openTime: k.OpenTime,
                openPrice: k.OpenPrice,
                highPrice: k.HighPrice,
                lowPrice: k.LowPrice,
                closePrice: k.ClosePrice,
                volume: k.Volume,
                closeTime: k.CloseTime,
                quoteAssetVolume: k.QuoteVolume,
                numberOfTrades: k.TradeCount,
                takerBuyBaseVolume: k.TakerBuyBaseVolume,
                takerBuyQuoteVolume: k.TakerBuyQuoteVolume)).ToList();

            totalInserted += await repository.BulkInsert(tableName, candles, ct);
            logger.LogDebug("Inserted {Count} candles for {Symbol}", candles.Count, plan.Series.Symbol);

            from = closed[^1].OpenTime + intervalSpan;

            if (data.Count < MaxKlineLimit)
                return (totalInserted, true);
        }

        return (totalInserted, false); // only reached when cancelled
    }

    private static int GetKlineWeight(MarketTypeEnum market, int limit)
    {
        if (market == MarketTypeEnum.Spot)
            return 2;

        return limit switch
        {
            < 100 => 1,
            < 500 => 2,
            <= 1000 => 5,
            _ => 10,
        };
    }
}
