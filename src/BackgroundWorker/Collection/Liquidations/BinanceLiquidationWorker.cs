using System.Threading.Channels;
using Domain.Services;
using Application.Exchange;
using BackgroundWorker.Health;
using BackgroundWorker.SymbolData;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.Collection.Liquidations;

/// <summary>
/// Collects forced liquidation orders from the futures WebSocket stream.
/// <para>
/// There is no REST history for liquidations: only what arrives while this worker runs is stored, and Binance
/// pushes at most one liquidation per symbol per second (the latest in that second), so the series is a sample,
/// not a complete record.
/// </para>
/// The socket callback only enqueues; a single writer drains the queue in batches, so a slow database never
/// blocks the socket and the socket never blocks on I/O.
/// </summary>
public class BinanceLiquidationWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<BinanceLiquidationWorker> logger,
    SymbolDataProvider symbolDataProvider,
    ILiquidationStream liquidationStream,
    WorkerHealthState health) : BackgroundService
{
    private const int QueueCapacity = 50_000;
    private const int MaxBatchSize = 1_000;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(5);

    private Channel<(SymbolEnum Symbol, Liquidation Row)> _queue = null!;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<SymbolEnum> symbols;
        try
        {
            var plans = await symbolDataProvider.WaitForPlansAsync(stoppingToken);
            symbols = SymbolDataPlans.LiquidationsForBinance(plans, (plan, reason) =>
                logger.LogWarning("Liquidations are not collected for {Symbol}: {Reason}", plan.Symbol, reason)).ToList();

            if (symbols.Count == 0)
            {
                logger.LogInformation("No symbol asks for its liquidations; nothing to collect.");
                return;
            }

            await EnsureTablesAsync(symbols, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        _queue = Channel.CreateBounded<(SymbolEnum, Liquidation)>(
            new BoundedChannelOptions(QueueCapacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.DropWrite,
            },
            _ => logger.LogError("Liquidation queue is full; a liquidation was dropped"));

        try
        {
            await using var subscription = await SubscribeAsync(symbols, stoppingToken);
            await WriteBatchesAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            await FlushRemainingAsync();
        }
    }

    private async Task EnsureTablesAsync(List<SymbolEnum> symbols, CancellationToken ct)
    {
        foreach (var symbol in symbols)
        {
            var tableName = TableNameRegistry.GetLiquidationTableName(ExchangeEnum.Binance, symbol);

            await TableCreation.EnsureWithRetryAsync(async token =>
            {
                using var scope = scopeFactory.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IMarketDataRepository<Liquidation>>().EnsureTableExistsAsync(tableName, token);
            }, tableName, logger, ct);
        }
    }

    /// <summary>Subscribes, trying again until it works: a stream that never started would leave the table silently empty.</summary>
    private async Task<IAsyncDisposable> SubscribeAsync(List<SymbolEnum> symbols, CancellationToken ct)
    {
        var listener = new LiquidationListener(_queue.Writer, health, logger);

        while (true)
        {
            var result = await liquidationStream.SubscribeAsync(ExchangeEnum.Binance, symbols, listener, ct);

            if (result.Success)
            {
                health.SetConnected(HealthComponents.LiquidationStream, true);
                logger.LogInformation("Subscribed to liquidation stream for {Symbols}", string.Join(", ", symbols));
                return result.Value;
            }

            health.SetConnected(HealthComponents.LiquidationStream, false);
            logger.LogWarning("Liquidation subscription failed: {Error}. Retrying in {Delay}", result.Error, RetryDelay);
            await Task.Delay(RetryDelay, ct);
        }
    }

    /// <summary>What the exchange tells about the stream. Every method runs on the socket's thread: cheap, never blocking.</summary>
    private sealed class LiquidationListener(
        ChannelWriter<(SymbolEnum Symbol, Liquidation Row)> queue, WorkerHealthState health, ILogger logger) : ILiquidationListener
    {
        public void OnLiquidation(SymbolEnum symbol, Liquidation liquidation)
        {
            health.RecordMessage(HealthComponents.LiquidationStream);
            queue.TryWrite((symbol, liquidation));
        }

        public void OnConnectionLost()
        {
            health.SetConnected(HealthComponents.LiquidationStream, false);
            logger.LogWarning("Liquidation stream connection lost, reconnecting");
        }

        public void OnConnectionRestored(TimeSpan offlineFor)
        {
            health.SetConnected(HealthComponents.LiquidationStream, true);
            logger.LogInformation("Liquidation stream connection restored");
        }
    }

    private async Task WriteBatchesAsync(CancellationToken ct)
    {
        var batch = new List<(SymbolEnum Symbol, Liquidation Row)>(MaxBatchSize);

        while (await _queue.Reader.WaitToReadAsync(ct))
        {
            // Let a burst (liquidation cascades) accumulate so it becomes one COPY instead of many.
            await Task.Delay(BatchWindow, ct);

            batch.Clear();
            while (batch.Count < MaxBatchSize && _queue.Reader.TryRead(out var item))
                batch.Add(item);

            await InsertWithRetryAsync(batch, ct);
        }
    }

    private async Task InsertWithRetryAsync(List<(SymbolEnum Symbol, Liquidation Row)> batch, CancellationToken ct)
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
                // Keep the batch and try again; the queue absorbs new liquidations meanwhile.
                logger.LogError(ex, "Failed to store {Count} liquidations, retrying in {Delay}", batch.Count, RetryDelay);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }

    private async Task InsertAsync(List<(SymbolEnum Symbol, Liquidation Row)> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
            return;

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IMarketDataRepository<Liquidation>>();

        foreach (var group in batch.GroupBy(b => b.Symbol))
        {
            var tableName = TableNameRegistry.GetLiquidationTableName(ExchangeEnum.Binance, group.Key);
            var inserted = await repository.BulkInsert(tableName, group.Select(g => g.Row).ToList(), ct);
            logger.LogDebug("Inserted {Count} liquidations for {Symbol}", inserted, group.Key);
        }
    }

    private async Task FlushRemainingAsync()
    {
        if (_queue is null)
            return;

        var rest = new List<(SymbolEnum Symbol, Liquidation Row)>();
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
            logger.LogError(ex, "Could not store {Count} queued liquidations during shutdown", rest.Count);
        }
    }
}
