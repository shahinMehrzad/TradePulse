using Application.Services;
using Binance.Net.Clients;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
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
        IBinanceRestClient binanceRestClient) : BackgroundService
{
    private readonly IBinanceRestClient _binanceClient = binanceRestClient;
    private readonly BinanceExchangeConfigurations _binanceConfigurations = binanceConfigurations.Value;
    private const int MaxKlineLimit = 1000;
    private const int MaxParallelSymbols = 4;
    private const int MaxRetries = 5;

    private readonly Dictionary<MarketTypeEnum, BinanceWeightLimiter> _limiters = new()
    {
        [MarketTypeEnum.Futures] = new BinanceWeightLimiter(binanceConfigurations.Value.MaxLimit),
        [MarketTypeEnum.Spot] = new BinanceWeightLimiter(binanceConfigurations.Value.MaxLimit),
    };
    private static readonly TimeSpan CloseOffset = TimeSpan.FromSeconds(3);

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
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(GetDelayUntilNextClose(interval), ct);
                await SyncAllSymbolsAsync(symbols, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {            
        }
    }

    private static TimeSpan GetDelayUntilNextClose(TimeSpan interval)
    {
        var now = DateTime.UtcNow;
        var nextBoundary = new DateTime((now.Ticks / interval.Ticks + 1) * interval.Ticks, DateTimeKind.Utc);
        return nextBoundary + CloseOffset - now;
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
        var lastOpen = await repository.GetLatestOpenDataTime(tableName, ct);

        var from = lastOpen == DateTimeOffset.MinValue ? symbol.StartDate : lastOpen.UtcDateTime + intervalSpan;

        var limiter = _limiters[symbol.MarketType];
        var weight = GetKlineWeight(symbol.MarketType, MaxKlineLimit);
        var retry = 0;

        while (!ct.IsCancellationRequested)
        {
            if (from + intervalSpan > DateTime.UtcNow)
                return;

            await limiter.AcquireAsync(weight, ct);

            var result = symbol.MarketType == MarketTypeEnum.Futures
                ? await _binanceClient.UsdFuturesApi.ExchangeData.GetKlinesAsync(
                    symbol.Name.ToString(), (KlineInterval)symbol.Interval,
                    startTime: from, limit: MaxKlineLimit, ct: ct)
                : await _binanceClient.SpotApi.ExchangeData.GetKlinesAsync(
                    symbol.Name.ToString(), (KlineInterval)symbol.Interval,
                    startTime: from, limit: MaxKlineLimit, ct: ct);

            if (!result.Success)
            {
                logger.LogWarning("Kline request failed for {Symbol}: {Error}", symbol.Name, result.Error);
                if (++retry > MaxRetries)
                    return;
                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, retry)), ct); // exponential backoff
                continue;

            }

            retry = 0;
            var data = result.Data.ToList();
            if (data.Count == 0)
                return;

            var now = DateTime.UtcNow;
            var closed = data.Where(k => k.CloseTime < now).ToList();
            if (closed.Count == 0)
                return;

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

            await repository.BulkInsert(tableName, candles, ct);
            logger.LogDebug("Inserted {Count} candles for {Symbol}", candles.Count, symbol.Name);

            from = closed[^1].OpenTime + intervalSpan;

            if (data.Count < MaxKlineLimit)
                return;
        }
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

public sealed class BinanceWeightLimiter(int maxWeightPerMinute)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _used;
    private DateTime _windowStart = DateTime.UtcNow;

    public async Task AcquireAsync(int weight, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            while (true)
            {
                var now = DateTime.UtcNow;
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
                    await Task.Delay(wait, ct);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}