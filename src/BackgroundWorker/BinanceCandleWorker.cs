using Application.Services;
using BackgroundWorker.Health;
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

namespace BackgroundWorker;

public class BinanceCandleWorker(IServiceScopeFactory scopeFactory,
        ILogger<BinanceCandleWorker> logger,
        IOptions<BinanceExchangeConfigurations> binanceConfigurations,
        IBinanceRestClient binanceRestClient,
        IBinanceSocketClient binanceSocketClient,
        BinanceRateLimiters rateLimiters,
        WorkerHealthState health) : BackgroundService
{
    private readonly IBinanceRestClient _binanceClient = binanceRestClient;
    private readonly IBinanceSocketClient _binanceSocketClient = binanceSocketClient;
    private readonly BinanceExchangeConfigurations _binanceConfigurations = binanceConfigurations.Value;
    private const int MaxKlineLimit = 1000;
    private const int MaxParallelSymbols = 4;
    private const int MaxRetries = 5;

    private static readonly TimeSpan GapCheckPeriod = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        
        var groups = _binanceConfigurations.Symbols.Values
            .GroupBy(s => (int)s.Interval)
            .ToList();

        if (groups.Count == 0)
        {
            logger.LogWarning("No symbols configured for Binance candle worker.");
            return;
        }

        await Task.WhenAll(groups.Select(g =>
            RunIntervalLoopAsync(TimeSpan.FromSeconds(g.Key), g.ToList(), stoppingToken)));
    }

    private async Task RunIntervalLoopAsync(TimeSpan interval, List<BinanceSymbolConfigurations> symbols, CancellationToken ct)
    {
        try
        {
            await SyncAllSymbolsAsync(symbols, ct);
            await RepairGapsAsync(symbols, ct);
            var lastGapCheck = DateTime.UtcNow;

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(MarketClock.GetDelayUntilNextBoundary(interval, MarketClock.CloseOffset, DateTime.UtcNow), ct);
                await SyncAllSymbolsAsync(symbols, ct);

                if (DateTime.UtcNow - lastGapCheck >= GapCheckPeriod)
                {
                    await RepairGapsAsync(symbols, ct);
                    lastGapCheck = DateTime.UtcNow;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {            
        }
    }

    private async Task SyncAllSymbolsAsync(List<BinanceSymbolConfigurations> symbols, CancellationToken ct)
    {
        await Parallel.ForEachAsync(
            symbols,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSymbols, CancellationToken = ct },
            async (symbol, token) =>
            {
                try
                {
                    await SyncSymbolAsync(symbol, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Sync failed for {Symbol}", symbol.Name);
                }
            });
    }

    private async Task SyncSymbolAsync(BinanceSymbolConfigurations symbol, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICandleRepository>();

        var tableName = TableNameRegistry.GetCandleTableName(ExchangeEnum.Binance, symbol.MarketType, symbol.Name, symbol.Interval);
        var intervalSpan = TimeSpan.FromSeconds((int)symbol.Interval);

        // A failure here must surface: treating it as "table is empty" would re-download the whole history.
        var lastOpen = await repository.GetLatestOpenDataTime(tableName, ct);
        var from = lastOpen is null ? symbol.StartDate : lastOpen.Value.UtcDateTime + intervalSpan;

        await FetchRangeAsync(symbol, repository, tableName, from, to: null, ct);
    }

    private async Task RepairGapsAsync(List<BinanceSymbolConfigurations> symbols, CancellationToken ct)
    {
        await Parallel.ForEachAsync(
            symbols,
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelSymbols, CancellationToken = ct },
            async (symbol, token) =>
            {
                try
                {
                    await RepairSymbolGapsAsync(symbol, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Gap repair failed for {Symbol}", symbol.Name);
                }
            });
    }

    private async Task RepairSymbolGapsAsync(BinanceSymbolConfigurations symbol, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICandleRepository>();

        var tableName = TableNameRegistry.GetCandleTableName(ExchangeEnum.Binance, symbol.MarketType, symbol.Name, symbol.Interval);
        var intervalSpan = TimeSpan.FromSeconds((int)symbol.Interval);

        var gaps = await repository.GetGapsAsync(tableName, intervalSpan, ct);
        foreach (var gap in gaps)
        {
            var inserted = await FetchRangeAsync(symbol, repository, tableName,
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
    /// inclusive open time) and stores them. Returns the number of newly inserted rows.
    /// </summary>
    private async Task<int> FetchRangeAsync(
        BinanceSymbolConfigurations symbol, ICandleRepository repository, string tableName,
        DateTime from, DateTime? to, CancellationToken ct)
    {
        var intervalSpan = TimeSpan.FromSeconds((int)symbol.Interval);
        var limiter = rateLimiters.For(symbol.MarketType);
        var weight = GetKlineWeight(symbol.MarketType, MaxKlineLimit);
        var retry = 0;
        var totalInserted = 0;

        while (!ct.IsCancellationRequested)
        {
            if (from + intervalSpan > DateTime.UtcNow || (to is not null && from > to))
                return totalInserted;

            await limiter.AcquireAsync(weight, ct);

            // Binance treats endTime as inclusive on open time.
            var result = symbol.MarketType == MarketTypeEnum.Futures
                ? await _binanceClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol.Name.ToString(), (KlineInterval)symbol.Interval,
                    startTime: from, endTime: to, limit: MaxKlineLimit, ct: ct)
                : await _binanceClient.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol.Name.ToString(), (KlineInterval)symbol.Interval,
                    startTime: from, endTime: to, limit: MaxKlineLimit, ct: ct);

            if (!result.Success)
            {
                health.RecordFailure(HealthComponents.CandleRequests, result.Error?.Message);
                logger.LogWarning("Kline request failed for {Symbol}: {Error}", symbol.Name, result.Error);
                if (++retry > MaxRetries)
                    return totalInserted;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)), ct); // exponential backoff
                continue;
            }

            health.RecordSuccess(HealthComponents.CandleRequests);
            retry = 0;
            var data = result.Data.ToList();
            if (data.Count == 0)
                return totalInserted;

            var now = DateTime.UtcNow;
            var closed = data.Where(k => k.CloseTime < now).ToList();
            if (closed.Count == 0)
                return totalInserted;

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
            logger.LogDebug("Inserted {Count} candles for {Symbol}", candles.Count, symbol.Name);

            from = closed[^1].OpenTime + intervalSpan;

            if (data.Count < MaxKlineLimit)
                return totalInserted;
        }

        return totalInserted;
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

public sealed class BinanceWeightLimiter(int maxWeightPerMinute, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _used;
    private DateTime _windowStart = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;

    public async Task AcquireAsync(int weight, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            while (true)
            {
                var now = _timeProvider.GetUtcNow().UtcDateTime;
                if (now - _windowStart >= TimeSpan.FromMinutes(1))
                {
                    _windowStart = now;
                    _used = 0;
                }

                if (_used + weight <= maxWeightPerMinute)
                {
                    _used += weight;
                    return;
                }

                var wait = _windowStart.AddMinutes(1) - now;
                if (wait > TimeSpan.Zero)
                    await Task.Delay(wait, _timeProvider, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}