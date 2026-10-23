using Domain.Services;
using BackgroundWorker.Exchange.Binance;
using BackgroundWorker.Health;
using BackgroundWorker.Scheduling;
using Binance.Net.Interfaces.Clients;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Infrastructure.Configurations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.Collection.OpenInterests;

/// <summary>
/// Collects open interest snapshots for USD-M futures, one loop per period like the candle worker.
/// Binance serves only about the last 30 days of history, so anything older than the first run can never be
/// recovered: keep this worker running continuously.
/// </summary>
public class BinanceOpenInterestWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BinanceOpenInterestWorker> logger,
    IOptions<BinanceFuturesDataConfigurations> configurations,
    IBinanceRestClient binanceClient,
    BinanceRateLimiters rateLimiters,
    WorkerHealthState health) : BackgroundService
{
    private const int MaxLimit = 500;

    /// <summary>Slightly below the documented 30 days so the first request never falls outside the retained window.</summary>
    private static readonly TimeSpan MaxHistory = TimeSpan.FromDays(29);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var groups = configurations.Value.OpenInterests.Values
            .Where(IsSupported)
            .GroupBy(s => (int)s.Interval)
            .ToList();

        if (groups.Count == 0)
        {
            logger.LogWarning("No symbols configured for the open interest worker.");
            return;
        }

        await Task.WhenAll(groups.Select(g =>
        {
            var symbols = g.ToList();
            return MarketClock.RunEveryIntervalAsync(
                TimeSpan.FromSeconds(g.Key), ct => SyncAllAsync(symbols, ct), stoppingToken);
        }));
    }

    private bool IsSupported(OpenInterestConfiguration symbol)
    {
        if (BinancePeriodIntervals.TryMap(symbol.Interval, out _))
            return true;

        logger.LogError("Open interest is not available for {Symbol} at {Interval}; entry ignored", symbol.Name, symbol.Interval);
        return false;
    }

    private async Task SyncAllAsync(List<OpenInterestConfiguration> symbols, CancellationToken ct)
    {
        foreach (var symbol in symbols)
        {
            try
            {
                await SyncSymbolAsync(symbol, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Open interest sync failed for {Symbol} {Interval}", symbol.Name, symbol.Interval);
            }
        }
    }

    private async Task SyncSymbolAsync(OpenInterestConfiguration symbol, CancellationToken ct)
    {
        BinancePeriodIntervals.TryMap(symbol.Interval, out var period);

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMarketDataRepository<OpenInterest>>();
        var tableName = TableNameRegistry.GetOpenInterestTableName(ExchangeEnum.Binance, symbol.Name, symbol.Interval);
        var limiter = rateLimiters.For(MarketTypeEnum.Futures);

        var oldestAvailable = DateTime.UtcNow - MaxHistory;
        var latest = await repository.GetLatestTimeAsync(tableName, ct);
        var from = latest is null || latest.Value.AddMilliseconds(1) < oldestAvailable
            ? oldestAvailable
            : latest.Value.AddMilliseconds(1);

        while (!ct.IsCancellationRequested)
        {
            await limiter.AcquireAsync(1, ct);

            var result = await binanceClient.UsdFuturesApi.ExchangeData.GetOpenInterestHistoryAsync(
                symbol.Name.ToString(), period, limit: MaxLimit, startTime: from, ct: ct);

            if (!result.Success)
            {
                health.RecordFailure(HealthComponents.OpenInterestRequests, result.Error?.Message);
                // Resumable: the next period boundary continues from the newest stored snapshot.
                logger.LogWarning("Open interest request failed for {Symbol} {Interval}: {Error}", symbol.Name, symbol.Interval, result.Error);
                return;
            }

            health.RecordSuccess(HealthComponents.OpenInterestRequests);
            var data = result.Data.ToList();
            if (data.Count == 0)
                return;

            var rows = data
                .Where(r => r.Timestamp.HasValue)
                .Select(r => new OpenInterest(r.Timestamp!.Value, r.SumOpenInterest, r.SumOpenInterestValue))
                .ToList();
            if (rows.Count == 0)
                return;
            var inserted = await repository.BulkInsert(tableName, rows, ct);
            logger.LogDebug("Inserted {Count} open interest rows for {Symbol} {Interval}", inserted, symbol.Name, symbol.Interval);

            from = rows.Max(r => r.Time).AddMilliseconds(1);

            if (data.Count < MaxLimit)
                return;
        }
    }
}
