using Application.Exchange;
using BackgroundWorker.CandleSeries;
using Domain.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.Health;

/// <summary>
/// Independent connection probe. The data workers only talk to the exchange when a candle closes (a daily candle means
/// one request a day), so their traffic cannot tell whether the connection is alive right now. This worker:
/// <list type="bullet">
/// <item>pings the futures (and spot, when used) REST API on a fixed schedule;</item>
/// <item>keeps a busy stream open (the mark price ticks every few seconds) as proof that the futures WebSocket path works,
/// since the liquidation stream is too quiet to prove anything by itself.</item>
/// </list>
/// It only reports into <see cref="WorkerHealthState"/> and never touches the database.
/// </summary>
public class BinanceConnectivityWorker(
    ILogger<BinanceConnectivityWorker> logger,
    WorkerHealthState health,
    IExchangeProbe probe,
    IOptions<HealthConfigurations> healthOptions,
    CandleSeriesProvider seriesProvider) : BackgroundService
{
    private static readonly TimeSpan SubscribeRetryDelay = TimeSpan.FromSeconds(10);

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
            await PingAsync(HealthComponents.FuturesRest, MarketTypeEnum.Futures, ct);

            // Spot is only pinged while a spot series is being collected (the series are known after startup).
            if (seriesProvider.Current.ForExchange(ExchangeEnum.Binance).Any(s => s.Series.MarketType == MarketTypeEnum.Spot))
                await PingAsync(HealthComponents.SpotRest, MarketTypeEnum.Spot, ct);
        }
        while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task PingAsync(string component, MarketTypeEnum market, CancellationToken ct)
    {
        try
        {
            var result = await probe.PingAsync(ExchangeEnum.Binance, market, ct);

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
        var listener = new ProbeListener(health);

        while (!ct.IsCancellationRequested)
        {
            var result = await probe.SubscribeToProbeStreamAsync(ExchangeEnum.Binance, symbol, listener, ct);

            if (!result.Success)
            {
                health.SetConnected(HealthComponents.FuturesWebSocket, false);
                logger.LogWarning("Probe stream subscription failed: {Error}. Retrying in {Delay}", result.Error, SubscribeRetryDelay);
                await Task.Delay(SubscribeRetryDelay, ct);
                continue;
            }

            await using var subscription = result.Value;
            health.SetConnected(HealthComponents.FuturesWebSocket, true);

            // The exchange adapter reconnects on its own; this task only has to keep the subscription alive.
            await Task.Delay(Timeout.Infinite, ct);
        }
    }

    /// <summary>What the exchange tells about the probe stream. Every method runs on the socket's thread: cheap, never blocking.</summary>
    private sealed class ProbeListener(WorkerHealthState health) : IProbeStreamListener
    {
        public void OnMessage() => health.RecordMessage(HealthComponents.FuturesWebSocket);

        public void OnConnectionLost() => health.SetConnected(HealthComponents.FuturesWebSocket, false);

        public void OnConnectionRestored(TimeSpan offlineFor) => health.SetConnected(HealthComponents.FuturesWebSocket, true);
    }
}
