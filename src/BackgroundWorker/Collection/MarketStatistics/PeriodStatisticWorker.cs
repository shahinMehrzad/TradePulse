using Application.Exchange;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Health;
using BackgroundWorker.Plans;
using BackgroundWorker.Scheduling;
using Domain.Enums;
using Domain.Repositories;
using Domain.Services;
using Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.Collection.MarketStatistics;

/// <summary>
/// Collects a statistic the exchange publishes once per period (long/short ratio, taker volume), one loop per period like the
/// open interest worker. Which series it runs for is the admin's choice: the series flagged "collect market statistics". The
/// exchange serves only a limited history (<see cref="IPeriodStatisticGateway{TRow}.MaxHistory"/>), so anything older than the
/// first run can never be recovered: keep this worker running continuously.
/// </summary>
public abstract class PeriodStatisticWorker<TRow>(
    IServiceScopeFactory scopeFactory,
    ILogger logger,
    CandleSeriesProvider seriesProvider,
    IPeriodStatisticGateway<TRow> gateway,
    WorkerHealthState health,
    CollectionStats stats) : BackgroundService
{
    /// <summary>For logs, e.g. "long/short ratio".</summary>
    protected abstract string Name { get; }

    protected abstract MarketDataTypeEnum DataType { get; }

    protected abstract string HealthComponent { get; }

    protected abstract string TableName(Domain.ValueObjects.CandleSeries series);

    protected abstract DateTime TimeOf(TRow row);

    // One loop per time frame; only the series matter, so a change of a start date is no concern of this worker.
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        PlanRunner.RunGroupedAsync(seriesProvider,
            plans => MarketStatisticsPlans.SelectForBinance(plans, DataType, (s, reason) =>
                logger.LogWarning("{Name} is not collected for {Symbol} {Interval}: {Reason}", Name, s.Series.Symbol, s.Series.Timeframe, reason))
                .Select(p => p with { StartDate = default, CollectOpenInterest = false }).ToList(),
            plan => plan.Series.Timeframe,
            CollectAsync, logger, $"{Name} series", stoppingToken);

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
            var tableName = TableName(plan.Series);

            await TableCreation.EnsureWithRetryAsync(async token =>
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IMarketDataRepository<TRow>>().EnsureTableExistsAsync(tableName, token);
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
                logger.LogError(ex, "{Name} sync failed for {Symbol} {Interval}", Name, plan.Series.Symbol, plan.Series.Timeframe);
            }
        }
    }

    private async Task SyncSeriesAsync(CandleSeriesPlan plan, CancellationToken ct)
    {
        var series = plan.Series;

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMarketDataRepository<TRow>>();
        var tableName = TableName(series);

        var oldestAvailable = DateTime.UtcNow - gateway.MaxHistory;
        var latest = await repository.GetLatestTimeAsync(tableName, ct);
        var from = latest is null || latest.Value.AddMilliseconds(1) < oldestAvailable
            ? oldestAvailable
            : latest.Value.AddMilliseconds(1);

        while (!ct.IsCancellationRequested)
        {
            var result = await gateway.GetAsync(series.Exchange, series.Symbol, series.Timeframe, from, ct);

            if (!result.Success)
            {
                health.RecordFailure(HealthComponent, result.Error);
                // Resumable: the next period boundary continues from the newest stored row.
                logger.LogWarning("{Name} request failed for {Symbol} {Interval}: {Error}", Name, series.Symbol, series.Timeframe, result.Error);
                return;
            }

            health.RecordSuccess(HealthComponent);
            var page = result.Value;
            if (page.Items.Count == 0)
                return;

            var inserted = await repository.BulkInsert(tableName, page.Items, ct);
            stats.Stored(DataType, inserted);
            logger.LogDebug("Inserted {Count} {Name} rows for {Symbol} {Interval}", inserted, Name, series.Symbol, series.Timeframe);

            from = page.Items.Max(TimeOf).AddMilliseconds(1);

            if (!page.HasMore)
                return;
        }
    }
}
