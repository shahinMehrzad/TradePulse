using BackgroundWorker.CandleSeries;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.Health;

/// <summary>
/// Writes one line to the log every <see cref="HealthConfigurations.SummaryIntervalMinutes"/> minutes saying what the collector
/// has done: how much it stored, how far the newest candle is behind, and whether the live streams are up. The collectors log
/// nothing per batch (that would drown the log), so this is how a person reading it sees that data is flowing. Problems are
/// not repeated here: a change of health is logged, as a warning or an error, the moment it happens.
/// </summary>
public sealed class CollectorSummaryWorker(
    ILogger<CollectorSummaryWorker> logger,
    IOptions<HealthConfigurations> options,
    CollectionStats stats,
    CandleSeriesProvider seriesProvider,
    CandleFreshnessMonitor freshness,
    WorkerHealthState health,
    TimeProvider timeProvider) : BackgroundService
{
    private static readonly string[] StreamComponents =
    [
        HealthComponents.CandleStreamFutures, HealthComponents.CandleStreamSpot,
        HealthComponents.LiquidationStream, HealthComponents.FuturesWebSocket,
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = options.Value.SummaryIntervalMinutes;
        if (minutes <= 0)
            return;

        var period = TimeSpan.FromMinutes(minutes);
        using var timer = new PeriodicTimer(period, timeProvider);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await LogSummaryAsync(period, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task LogSummaryAsync(TimeSpan period, CancellationToken ct)
    {
        try
        {
            var stored = stats.TakeSinceLastCall();
            var series = seriesProvider.Current.Select(p => p.Series).Distinct().Count();

            var streams = StreamComponents.Select(health.Get).Where(s => s.Connected is not null).ToList();
            var streamsUp = streams.Count(s => s.Connected == true);

            IReadOnlyList<TableFreshness>? tables = null;
            try
            {
                tables = series == 0 ? [] : await freshness.GetAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The database is not answering; the health checks say so loudly. The rest of the summary is still worth writing.
            }

            if (series == 0)
            {
                logger.LogInformation(
                    "Last {Minutes} min: stored {Candles} candles, {FundingRates} funding rates, {OpenInterest} open interest, {Liquidations} liquidations. No candle series configured yet. Streams up {StreamsUp}/{StreamsTotal}.",
                    (int)period.TotalMinutes, stored.Candles, stored.FundingRates, stored.OpenInterest, stored.Liquidations, streamsUp, streams.Count);
            }
            else if (tables is null)
            {
                logger.LogInformation(
                    "Last {Minutes} min: stored {Candles} candles, {FundingRates} funding rates, {OpenInterest} open interest, {Liquidations} liquidations. {Series} candle series, freshness unknown (database not answering). Streams up {StreamsUp}/{StreamsTotal}.",
                    (int)period.TotalMinutes, stored.Candles, stored.FundingRates, stored.OpenInterest, stored.Liquidations, series, streamsUp, streams.Count);
            }
            else
            {
                var newest = tables.Select(t => t.LatestOpen).Where(l => l is not null).DefaultIfEmpty(null).Max();
                var behind = tables.Count(t => t.CandlesBehind > 0);
                var mostBehind = tables.Select(t => t.CandlesBehind).DefaultIfEmpty(0).Max();

                logger.LogInformation(
                    "Last {Minutes} min: stored {Candles} candles, {FundingRates} funding rates, {OpenInterest} open interest, {Liquidations} liquidations. {Series} candle series, {Behind} behind (at most {MostBehind} candles), newest candle {Newest:yyyy-MM-dd HH:mm}. Streams up {StreamsUp}/{StreamsTotal}.",
                    (int)period.TotalMinutes, stored.Candles, stored.FundingRates, stored.OpenInterest, stored.Liquidations,
                    series, behind, mostBehind, newest, streamsUp, streams.Count);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An exception out of a worker would stop the whole host; a summary is never worth that.
            logger.LogWarning(ex, "Could not write the collector summary");
        }
    }
}
