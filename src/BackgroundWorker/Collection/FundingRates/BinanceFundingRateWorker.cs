using Application.Services;
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

namespace BackgroundWorker.Collection.FundingRates;

/// <summary>
/// Collects funding rate settlements (every 8 h, some symbols 4 h or 1 h) for USD-M futures.
/// Polls hourly and resumes from the newest stored settlement; the full history is available from the API.
/// </summary>
public class BinanceFundingRateWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BinanceFundingRateWorker> logger,
    IOptions<BinanceFuturesDataConfigurations> configurations,
    IBinanceRestClient binanceClient,
    BinanceRateLimiters rateLimiters,
    WorkerHealthState health) : BackgroundService
{
    private const int MaxLimit = 1000;
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var symbols = configurations.Value.FundingRates.Values.ToList();
        if (symbols.Count == 0)
        {
            logger.LogWarning("No symbols configured for the funding rate worker.");
            return Task.CompletedTask;
        }

        return MarketClock.RunEveryIntervalAsync(PollInterval, ct => SyncAllAsync(symbols, ct), stoppingToken);
    }

    private async Task SyncAllAsync(List<FundingRateConfiguration> symbols, CancellationToken ct)
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
                logger.LogError(ex, "Funding rate sync failed for {Symbol}", symbol.Name);
            }
        }
    }

    private async Task SyncSymbolAsync(FundingRateConfiguration symbol, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMarketDataRepository<FundingRate>>();
        var tableName = TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, symbol.Name);
        var limiter = rateLimiters.For(MarketTypeEnum.Futures);

        var latest = await repository.GetLatestTimeAsync(tableName, ct);
        var from = latest is null ? symbol.StartDate : latest.Value.AddMilliseconds(1);

        while (!ct.IsCancellationRequested && from < DateTime.UtcNow)
        {
            await limiter.AcquireAsync(1, ct);

            var result = await binanceClient.UsdFuturesApi.ExchangeData.GetFundingRatesAsync(
                symbol.Name.ToString(), startTime: from, limit: MaxLimit, ct: ct);

            if (!result.Success)
            {
                health.RecordFailure(HealthComponents.FundingRateRequests, result.Error?.Message);
                // Resumable: the next poll continues from the newest stored settlement.
                logger.LogWarning("Funding rate request failed for {Symbol}: {Error}", symbol.Name, result.Error);
                return;
            }

            health.RecordSuccess(HealthComponents.FundingRateRequests);
            var data = result.Data.ToList();
            if (data.Count == 0)
                return;

            var rows = data.Select(r => new FundingRate(r.FundingTime, r.FundingRate, r.MarkPrice ?? 0m)).ToList();
            var inserted = await repository.BulkInsert(tableName, rows, ct);
            logger.LogDebug("Inserted {Count} funding rates for {Symbol}", inserted, symbol.Name);

            from = rows.Max(r => r.FundingTime).AddMilliseconds(1);

            if (data.Count < MaxLimit)
                return;
        }
    }
}
