using Application.Exchange;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Health;
using BackgroundWorker.Plans;
using BackgroundWorker.Scheduling;
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
/// The exchange serves only a limited history (<see cref="IOpenInterestGateway.MaxHistory"/>), so anything older than the
/// first run can never be recovered: keep this worker running continuously.
/// </summary>
public class BinanceOpenInterestWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BinanceOpenInterestWorker> logger,
    CandleSeriesProvider seriesProvider,
    IOpenInterestGateway gateway,
    WorkerHealthState health,
    CollectionStats stats) : BackgroundService
{    
    // One loop per time frame; only the series matter, so a change of a start date is no concern of this worker.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        PlanRunner.RunGroupedAsync(seriesProvider,
            plans => OpenInterestPlans.SelectForBinance(plans, (s, reason) =>
                logger.LogWarning("Open interest is not collected for {Symbol} {Interval}: {Reason}", s.Series.Symbol, s.Series.Timeframe, reason))
                .Select(p => p with { StartDate = default, CollectMarketStatistics = false }).ToList(),
            plan => plan.Series.Timeframe,
            CollectAsync, logger, "open interest series", stoppingToken);

    private async Task CollectAsync(TimeFrameEnum timeframe, IReadOnlyList<CandleSeriesPlan> series, CancellationToken stoppingToken)
    {
        try
        {
            await EnsureTablesAsync(series, stoppingToken);
            var forPeriod = series.ToList();
            await MarketClock.RunEveryIntervalAsync(TimeSpan.FromSeconds((int)timeframe), ct => SyncAllAsync(forPeriod, ct), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task EnsureTablesAsync(IReadOnlyList<CandleSeriesPlan> series, CancellationToken ct)
    {
        foreach (var plan in series)
        {
            var tableName = TableNameRegistry.GetOpenInterestTableName(plan.Series.Exchange, plan.Series.Symbol.Value, plan.Series.Timeframe);

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
                logger.LogError(ex, "Open interest sync failed for {Symbol} {Interval}", plan.Series.Symbol.Value, plan.Series.Timeframe);
            }
        }
    }

    private async Task SyncSeriesAsync(CandleSeriesPlan plan, CancellationToken ct)
    {
        var series = plan.Series;

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMarketDataRepository<OpenInterest>>();
        var tableName = TableNameRegistry.GetOpenInterestTableName(series.Exchange, series.Symbol.Value, series.Timeframe);

        var oldestAvailable = DateTime.UtcNow - gateway.MaxHistory;
        var latest = await repository.GetLatestTimeAsync(tableName, ct);
        var from = latest is null || latest.Value.AddMilliseconds(1) < oldestAvailable
            ? oldestAvailable
            : latest.Value.AddMilliseconds(1);

        while (!ct.IsCancellationRequested)
        {
            var result = await gateway.GetOpenInterestAsync(series.Exchange, series.Symbol, series.Timeframe, from, ct);

            if (!result.Success)
            {
                health.RecordFailure(HealthComponents.OpenInterestRequests, result.Error);
                // Resumable: the next period boundary continues from the newest stored snapshot.
                logger.LogWarning("Open interest request failed for {Symbol} {Interval}: {Error}", series.Symbol, series.Timeframe, result.Error);
                return;
            }

            health.RecordSuccess(HealthComponents.OpenInterestRequests);
            var page = result.Value;
            if (page.Items.Count == 0)
                return;

            var inserted = await repository.BulkInsert(tableName, page.Items, ct);
            stats.Stored(MarketDataTypeEnum.OpenInterest, inserted);
            logger.LogDebug("Inserted {Count} open interest rows for {Symbol} {Interval}", inserted, series.Symbol, series.Timeframe);

            from = page.Items.Max(r => r.Time).AddMilliseconds(1);

            if (!page.HasMore)
                return;
        }
    }
}
