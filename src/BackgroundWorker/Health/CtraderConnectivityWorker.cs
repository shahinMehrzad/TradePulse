using Application.Exchange;
using BackgroundWorker.CandleSeries;
using Domain.Enums;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.Health;

/// <summary>
/// The connection probe of Pepperstone (cTrader): the data workers only ask when a candle closes, so their traffic cannot tell whether the
/// connection is alive, or whether the access token (it expires) still works. This worker asks on a fixed schedule, but only while a Pepperstone
/// series is collected, so nothing connects for an exchange nobody uses. A series that is chosen while cTrader is not configured is reported as a
/// failure too: otherwise the collector would sit silent on it.
/// </summary>
public class CtraderConnectivityWorker(
    ILogger<CtraderConnectivityWorker> logger,
    WorkerHealthState health,
    IExchangeProbe probe,
    IOptions<HealthConfigurations> healthOptions,
    CandleSeriesProvider seriesProvider) : BackgroundService
{
    internal const string NotConfigured = "A Pepperstone series is collected, but cTrader is not configured (client id, secret, access token and account id).";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, healthOptions.Value.ProbeIntervalSeconds)));

        try
        {
            do
            {
                await ProbeOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ProbeOnceAsync(CancellationToken ct)
    {
        if (!seriesProvider.Current.ForExchange(ExchangeEnum.Pepperstone).Any())
            return;

        if (!probe.Serves(ExchangeEnum.Pepperstone))
        {
            health.RecordFailure(HealthComponents.CtraderConnection, NotConfigured);
            return;
        }

        try
        {
            var result = await probe.PingAsync(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, ct);

            if (result.Success)
                health.RecordSuccess(HealthComponents.CtraderConnection);
            else
                health.RecordFailure(HealthComponents.CtraderConnection, result.Error);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An exception here would stop the host.
            logger.LogError(ex, "The cTrader probe failed");
            health.RecordFailure(HealthComponents.CtraderConnection, ex.GetType().Name);
        }
    }
}
