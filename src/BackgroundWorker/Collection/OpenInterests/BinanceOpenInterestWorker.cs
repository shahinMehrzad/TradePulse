using BackgroundWorker.CandleSeries;
using BackgroundWorker.Exchange.Binance;
using BackgroundWorker.Health;
using BackgroundWorker.Scheduling;
using Binance.Net.Interfaces.Clients;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Domain.Services;
using Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.Collection.OpenInterests;

/// <summary>
/// Collects open interest snapshots for USD-M futures, one loop per period like the candle worker. Which series it runs
/// for is the admin's choice: the candle series flagged "collect open interest" (see <see cref="OpenInterestPlans"/>).
/// Binance serves only about the last 30 days of history, so anything older than the first run can never be
/// recovered: keep this worker running continuously.
/// </summary>
public class BinanceOpenInterestWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BinanceOpenInterestWorker> logger,
    CandleSeriesProvider seriesProvider,
    IBinanceRestClient binanceClient,
    BinanceRateLimiters rateLimiters,
    WorkerHealthState health) : BackgroundService
{
    private const int MaxLimit = 500;

    /// <summary>Slightly below the documented 30 days so the first request never falls outside the retained window.</summary>
    private static readonly TimeSpan MaxHistory = TimeSpan.FromDays(29);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // The same list the candle workers wait for: the flag lives on the candle series rows.
            var plans = await seriesProvider.WaitForSeriesAsync(stoppingToken);
            var series = OpenInterestPlans.SelectForBinance(plans, (s, reason) =>
                logger.LogWarning("Open interest is not collected for {Symbol} {Interval}: {Reason}", s.Series.Symbol, s.Series.Timeframe, reason));

            if (series.Count == 0)
            {
                logger.LogInformation("No candle series asks for open interest; nothing to collect.");
                return;
            }

            await EnsureTablesAsync(series, stoppingToken);

            await Task.WhenAll(series.GroupBy(p => p.Series.Timeframe).Select(group =>
            {
                var forPeriod = group.ToList();
                return MarketClock.RunEveryIntervalAsync(
                    TimeSpan.FromSeconds((int)group.Key), ct => SyncAllAsync(forPeriod, ct), stoppingToken);
            }));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task EnsureTablesAsync(IReadOnlyList<CandleSeriesPlan> series, CancellationToken ct)
    {
        foreach (var plan in series)
        {
            var tableName = TableNameRegistry.GetOpenInterestTableName(plan.Series.Exchange, plan.Series.Symbol, plan.Series.Timeframe);

            await TableCreation.EnsureWithRetryAsync(async token =>
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IMarketDataRepository<OpenInterest>>().EnsureTableExistsAsync(tableName, token);
            }, tableName, logger, ct);
        }
    }

    private async Task SyncAllAsync(List<CandleSeriesPlan> series, CancellationToken ct)
    {
        foreach (var plan in series)
        {
            try
            {
                await SyncSeriesAsync(plan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Open interest sync failed for {Symbol} {Interval}", plan.Series.Symbol, plan.Series.Timeframe);
            }
        }
    }

    private async Task SyncSeriesAsync(CandleSeriesPlan plan, CancellationToken ct)
    {
        var series = plan.Series;
        BinancePeriodIntervals.TryMap(series.Timeframe, out var period);

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMarketDataRepository<OpenInterest>>();
        var tableName = TableNameRegistry.GetOpenInterestTableName(series.Exchange, series.Symbol, series.Timeframe);
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
                series.Symbol.ToString(), period, limit: MaxLimit, startTime: from, ct: ct);

            if (!result.Success)
            {
                health.RecordFailure(HealthComponents.OpenInterestRequests, result.Error?.Message);
                // Resumable: the next period boundary continues from the newest stored snapshot.
                logger.LogWarning("Open interest request failed for {Symbol} {Interval}: {Error}", series.Symbol, series.Timeframe, result.Error);
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
            logger.LogDebug("Inserted {Count} open interest rows for {Symbol} {Interval}", inserted, series.Symbol, series.Timeframe);

            from = rows.Max(r => r.Time).AddMilliseconds(1);

            if (data.Count < MaxLimit)
                return;
        }
    }
}
