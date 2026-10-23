using System.Threading.Channels;
using Application.Exchange;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Health;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.Collection.Candles;

/// <summary>
/// Stores closed candles straight from the kline WebSocket, moments after they close, instead of waiting for the REST
/// loop (<see cref="BinanceCandleWorker"/>). The REST loop stays on as the safety net: it still runs at every boundary
/// and, after a socket reconnect, is asked to resync immediately (<see cref="CandleSyncCoordinator"/>).
/// <para>
/// The socket callback only enqueues into a bounded <see cref="Channel{T}"/>; one writer drains it in batches, so the
/// socket never waits on the database. Live candles are dropped for a table until its first REST sync has finished,
/// because a live candle in a table that is not caught up would hide the missing history from the resume logic.
/// </para>
/// </summary>
public class BinanceLiveCandleWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BinanceLiveCandleWorker> logger,
    IOptions<LiveCandleSettings> settings,
    CandleSeriesProvider seriesProvider,
    ILiveCandleStream liveStream,
    CandleSyncCoordinator coordinator,
    WorkerHealthState health,
    CollectionStats stats) : BackgroundService
{
    private const int QueueCapacity = 10_000;
    private const int MaxBatchSize = 500;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    private Channel<(string Table, Candle Row)> _queue = null!;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!settings.Value.LiveCandles)
        {
            logger.LogInformation("Live candle stream is disabled; REST sync only.");
            return;
        }

        IReadOnlyList<CandleSeriesPlan> series;
        try
        {
            series = (await seriesProvider.WaitForSeriesAsync(stoppingToken)).ForExchange(ExchangeEnum.Binance).ToList();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        _queue = Channel.CreateBounded<(string, Candle)>(
            new BoundedChannelOptions(QueueCapacity) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite },
            // A dropped candle is not lost: the REST loop fetches it at the boundary.
            _ => logger.LogError("Live candle queue is full; a candle was dropped (REST will fetch it)"));

        // One subscription per market and interval, each covering all of its symbols.
        var streams = series
            .GroupBy(p => (p.Series.MarketType, p.Series.Timeframe))
            .Select(g => new CandleStream(g.Key.MarketType, g.Key.Timeframe,
                g.DistinctBy(p => p.Series.Symbol).ToDictionary(p => p.Series.Symbol, p => p.Series.TableName)))
            .ToList();

        try
        {
            var subscriptions = await Task.WhenAll(streams.Select(s => SubscribeAsync(s, stoppingToken)));
            try
            {
                await WriteBatchesAsync(stoppingToken);
            }
            finally
            {
                foreach (var subscription in subscriptions)
                    await subscription.DisposeAsync();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await FlushRemainingAsync();
        }
    }

    private sealed record CandleStream(MarketTypeEnum Market, TimeFrameEnum Timeframe, Dictionary<SymbolEnum, string> TablesBySymbol)
    {
        public string Component => Market == MarketTypeEnum.Futures ? HealthComponents.CandleStreamFutures : HealthComponents.CandleStreamSpot;
    }

    /// <summary>Subscribes, trying again until it works: a stream that never started would silently leave the REST loop alone.</summary>
    private async Task<IAsyncDisposable> SubscribeAsync(CandleStream stream, CancellationToken ct)
    {
        var subscription = new LiveCandleSubscription(ExchangeEnum.Binance, stream.Market, stream.Timeframe, [.. stream.TablesBySymbol.Keys]);
        var listener = new StreamListener(stream, _queue.Writer, health, coordinator, logger);

        while (true)
        {
            var result = await liveStream.SubscribeAsync(subscription, listener, ct);

            if (result.Success)
            {
                health.SetConnected(stream.Component, true);
                logger.LogInformation("Subscribed to {Interval} candles for {Market}: {Symbols}",
                    stream.Timeframe, stream.Market, string.Join(", ", stream.TablesBySymbol.Keys));
                return result.Value;
            }

            health.SetConnected(stream.Component, false);
            logger.LogWarning("Candle stream subscription failed ({Market} {Interval}): {Error}. Retrying in {Delay}",
                stream.Market, stream.Timeframe, result.Error, RetryDelay);
            await Task.Delay(RetryDelay, ct);
        }
    }

    /// <summary>What the exchange tells about one stream. Every method runs on the socket's thread: cheap, never blocking.</summary>
    private sealed class StreamListener(
        CandleStream stream, ChannelWriter<(string Table, Candle Row)> queue,
        WorkerHealthState health, CandleSyncCoordinator coordinator, ILogger logger) : ILiveCandleListener
    {
        public void OnMessage() => health.RecordMessage(stream.Component);

        public void OnClosedCandle(Domain.ValueObjects.CandleSeries series, Candle candle)
        {
            if (stream.TablesBySymbol.TryGetValue(series.Symbol, out var table))
                queue.TryWrite((table, candle));
        }

        public void OnConnectionLost()
        {
            health.SetConnected(stream.Component, false);
            logger.LogWarning("Candle stream {Market} {Interval} lost, reconnecting", stream.Market, stream.Timeframe);
        }

        public void OnConnectionRestored(TimeSpan offlineFor)
        {
            health.SetConnected(stream.Component, true);
            logger.LogInformation("Candle stream {Market} {Interval} restored after {Offline}; requesting resync",
                stream.Market, stream.Timeframe, offlineFor);
            coordinator.RequestResync();
        }
    }

    private async Task WriteBatchesAsync(CancellationToken ct)
    {
        var batch = new List<(string Table, Candle Row)>(MaxBatchSize);

        while (await _queue.Reader.WaitToReadAsync(ct))
        {
            // Many symbols close at the same instant; gather them into one COPY per table.
            await Task.Delay(BatchWindow, ct);

            batch.Clear();
            while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out var item))
                batch.Add(item);

            await InsertWithRetryAsync(batch, ct);
        }
    }

    private async Task InsertWithRetryAsync(List<(string Table, Candle Row)> batch, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await InsertAsync(batch, ct);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the batch and try again; the queue absorbs new candles meanwhile. REST would also cover them.
                logger.LogError(ex, "Failed to store {Count} live candles, retrying in {Delay}", batch.Count, RetryDelay);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    private async Task InsertAsync(List<(string Table, Candle Row)> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
            return;

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ICandleRepository>();

        foreach (var group in batch.GroupBy(b => b.Table))
        {
            if (!coordinator.IsReady(group.Key))
            {
                logger.LogDebug("Skipping live candles for {Table}: initial sync not finished, REST will fetch them", group.Key);
                continue;
            }

            var inserted = await repository.BulkInsert(group.Key, group.Select(g => g.Row).ToList(), ct);
            stats.Stored(MarketDataTypeEnum.Candle, inserted);
            logger.LogDebug("Inserted {Count} live candles into {Table}", inserted, group.Key);
        }
    }

    private async Task FlushRemainingAsync()
    {
        if (_queue is null)
            return;

        var rest = new List<(string Table, Candle Row)>();
        while (_queue.Reader.TryRead(out var item))
            rest.Add(item);

        if (rest.Count == 0)
            return;

        using var timeout = new CancellationTokenSource(ShutdownFlushTimeout);
        try
        {
            await InsertAsync(rest, timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not store {Count} queued live candles during shutdown", rest.Count);
        }
    }
}
