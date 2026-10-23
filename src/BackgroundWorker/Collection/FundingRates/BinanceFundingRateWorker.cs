using BackgroundWorker.Exchange.Binance;
using BackgroundWorker.Health;
using BackgroundWorker.Scheduling;
using BackgroundWorker.SymbolData;
using Binance.Net.Interfaces.Clients;
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
    IBinanceRestClient binanceClient,
    BinanceRateLimiters rateLimiters,
    WorkerHealthState health) : BackgroundService
{
    private const int MaxLimit = 1000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var plans = await symbolDataProvider.WaitForPlansAsync(stoppingToken);
            var targets = SymbolDataPlans.FundingRatesForBinance(plans, (plan, reason) =>
                logger.LogWarning("The funding rate is not collected for {Symbol}: {Reason}", plan.Symbol, reason));

            if (targets.Count == 0)
            {
                logger.LogInformation("No symbol asks for its funding rate; nothing to collect.");
                return;
            }

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
            var tableName = TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, target.Symbol);

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
        var tableName = TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, target.Symbol);
        var limiter = rateLimiters.For(MarketTypeEnum.Futures);

        var latest = await repository.GetLatestTimeAsync(tableName, ct);
        var from = latest is null ? target.StartDate : latest.Value.AddMilliseconds(1);

        while (!ct.IsCancellationRequested && from < DateTime.UtcNow)
        {
            await limiter.AcquireAsync(1, ct);

            var result = await binanceClient.UsdFuturesApi.ExchangeData.GetFundingRatesAsync(
                target.Symbol.ToString(), startTime: from, limit: MaxLimit, ct: ct);

            if (!result.Success)
            {
                health.RecordFailure(HealthComponents.FundingRateRequests, result.Error?.Message);
                // Resumable: the next poll continues from the newest stored settlement.
                logger.LogWarning("Funding rate request failed for {Symbol}: {Error}", target.Symbol, result.Error);
                return;
            }

            health.RecordSuccess(HealthComponents.FundingRateRequests);
            var data = result.Data.ToList();
            if (data.Count == 0)
                return;

            var rows = data.Select(r => new FundingRate(r.FundingTime, r.FundingRate, r.MarkPrice ?? 0m)).ToList();
            var inserted = await repository.BulkInsert(tableName, rows, ct);
            logger.LogDebug("Inserted {Count} funding rates for {Symbol}", inserted, target.Symbol);

            from = rows.Max(r => r.FundingTime).AddMilliseconds(1);

            if (data.Count < MaxLimit)
                return;
        }
    }
}
