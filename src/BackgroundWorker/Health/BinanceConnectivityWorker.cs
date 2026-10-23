using BackgroundWorker.CandleSeries;
using BackgroundWorker.Exchange.Binance;
using Binance.Net.Interfaces.Clients;
using Domain.Enums;
using Infrastructure.Configurations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.Health;

/// <summary>
/// Independent connection probe. The data workers only talk to Binance when a candle closes (a daily candle means
/// one request a day), so their traffic cannot tell whether the connection is alive right now. This worker:
/// <list type="bullet">
/// <item>pings the futures (and spot, when used) REST API on a fixed schedule;</item>
/// <item>keeps a mark-price stream open (it ticks every few seconds) as proof that the futures WebSocket path works,
/// since the liquidation stream is too quiet to prove anything by itself.</item>
/// </list>
/// It only reports into <see cref="WorkerHealthState"/> and never touches the database.
/// </summary>
public class BinanceConnectivityWorker(
    ILogger<BinanceConnectivityWorker> logger,
    WorkerHealthState health,
    IBinanceRestClient restClient,
    IBinanceSocketClient socketClient,
    BinanceRateLimiters rateLimiters,
    IOptions<HealthConfigurations> healthOptions,
    CandleSeriesProvider seriesProvider) : BackgroundService
{
    private static readonly TimeSpan SubscribeRetryDelay = TimeSpan.FromSeconds(10);
    private const int MarkPriceIntervalMs = 3000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(PingLoopAsync(stoppingToken), ProbeStreamLoopAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, healthOptions.Value.ProbeIntervalSeconds)));

        do
        {
            await PingAsync(HealthComponents.FuturesRest, MarketTypeEnum.Futures,
                async token => { var r = await restClient.UsdFuturesApi.ExchangeData.PingAsync(token); return (r.Success, r.Error?.Message); }, ct);

            // Spot is only pinged while a spot series is being collected (the series are known after startup).
            if (seriesProvider.Current.Any(s => s.MarketType == MarketTypeEnum.Spot))
            {
                await PingAsync(HealthComponents.SpotRest, MarketTypeEnum.Spot,
                    async token => { var r = await restClient.SpotApi.ExchangeData.PingAsync(token); return (r.Success, r.Error?.Message); }, ct);
            }
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task PingAsync(
        string component, MarketTypeEnum market, Func<CancellationToken, Task<(bool Success, string? Error)>> ping, CancellationToken ct)
    {
        try
        {
            await rateLimiters.For(market).AcquireAsync(1, ct);
            var result = await ping(ct);

            if (result.Success)
                health.RecordSuccess(component);
            else
                health.RecordFailure(component, result.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            health.RecordFailure(component, ex.Message);
        }
    }

    private async Task ProbeStreamLoopAsync(CancellationToken ct)
    {
        var symbol = healthOptions.Value.ProbeSymbol;

        while (!ct.IsCancellationRequested)
        {
            var result = await socketClient.UsdFuturesApi.ExchangeData.SubscribeToMarkPriceUpdatesAsync(
                symbol, MarkPriceIntervalMs, _ => health.RecordMessage(HealthComponents.FuturesWebSocket), ct);

            if (!result.Success)
            {
                health.SetConnected(HealthComponents.FuturesWebSocket, false);
                logger.LogWarning("Probe stream subscription failed: {Error}. Retrying in {Delay}", result.Error, SubscribeRetryDelay);
                await Task.Delay(SubscribeRetryDelay, ct);
                continue;
            }

            var subscription = result.Data;
            subscription.ConnectionLost += () => health.SetConnected(HealthComponents.FuturesWebSocket, false);
            subscription.ConnectionRestored += _ => health.SetConnected(HealthComponents.FuturesWebSocket, true);
            health.SetConnected(HealthComponents.FuturesWebSocket, true);

            try
            {
                // The library reconnects on its own; this task only has to keep the subscription alive.
                await Task.Delay(Timeout.Infinite, ct);
            }
            finally
            {
                await socketClient.UnsubscribeAsync(subscription);
            }
        }
    }
}
