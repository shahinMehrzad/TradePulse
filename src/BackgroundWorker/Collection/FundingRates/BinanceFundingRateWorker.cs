using Application.Exchange;
using BackgroundWorker.Health;
using BackgroundWorker.Plans;
using BackgroundWorker.Scheduling;
using BackgroundWorker.SymbolData;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.Collection.FundingRates;

/// <summary>
/// Collects funding rate settlements (every 8 h, some symbols 4 h or 1 h) for USD-M futures, for the symbols the admin chose
/// (see <see cref="SymbolDataPlans"/>). Polls hourly and resumes from the newest stored settlement; the full history is
/// available from the API.
/// </summary>
public class BinanceFundingRateWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BinanceFundingRateWorker> logger,
    SymbolDataProvider symbolDataProvider,
    IFundingRateGateway gateway,
    WorkerHealthState health,
    CollectionStats stats) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    /// <summary>Only a change in which symbols ask for their funding rate (or from when) restarts this worker (see <see cref="PlanRunner"/>).</summary>
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        PlanRunner.RunAsync(symbolDataProvider,
            plans => SymbolDataPlans.FundingRatesForBinance(plans, (plan, reason) =>
                logger.LogWarning("The funding rate is not collected for {Symbol}: {Reason}", plan.Symbol, reason)),
            CollectAsync, logger, "funding rate symbols", stoppingToken);

    private async Task CollectAsync(IReadOnlyList<FundingRateTarget> targets, CancellationToken stoppingToken)
    {
        try
        {
            await EnsureTablesAsync(targets, stoppingToken);
            await MarketClock.RunEveryIntervalAsync(PollInterval, ct => SyncAllAsync(targets, ct), stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task EnsureTablesAsync(IReadOnlyList<FundingRateTarget> targets, CancellationToken ct)
    {
        foreach (var target in targets)
        {
            var tableName = TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, target.Symbol.Value);

            await TableCreation.EnsureWithRetryAsync(async token =>
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IMarketDataRepository<FundingRate>>().EnsureTableExistsAsync(tableName, token);
            }, tableName, logger, ct);
        }
    }

    private async Task SyncAllAsync(IReadOnlyList<FundingRateTarget> targets, CancellationToken ct)
    {
        foreach (var target in targets)
        {
            try
            {
                await SyncSymbolAsync(target, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Funding rate sync failed for {Symbol}", target.Symbol);
            }
        }
    }

    private async Task SyncSymbolAsync(FundingRateTarget target, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMarketDataRepository<FundingRate>>();
        var tableName = TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, target.Symbol.Value);

        var latest = await repository.GetLatestTimeAsync(tableName, ct);
        var from = latest is null ? target.StartDate : latest.Value.AddMilliseconds(1);

        while (!ct.IsCancellationRequested && from < DateTime.UtcNow)
        {
            var result = await gateway.GetFundingRatesAsync(ExchangeEnum.Binance, target.Symbol, from, ct);

            if (!result.Success)
            {
                health.RecordFailure(HealthComponents.FundingRateRequests, result.Error);
                // Resumable: the next poll continues from the newest stored settlement.
                logger.LogWarning("Funding rate request failed for {Symbol}: {Error}", target.Symbol, result.Error);
                return;
            }

            health.RecordSuccess(HealthComponents.FundingRateRequests);
            var page = result.Value;
            if (page.Items.Count == 0)
                return;

            var inserted = await repository.BulkInsert(tableName, page.Items, ct);
            stats.Stored(MarketDataTypeEnum.FundingRate, inserted);
            logger.LogDebug("Inserted {Count} funding rates for {Symbol}", inserted, target.Symbol);

            from = page.Items.Max(r => r.FundingTime).AddMilliseconds(1);

            if (!page.HasMore)
                return;
        }
    }
}
