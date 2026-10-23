using Application.Exchange;
using BackgroundWorker.Collection.Candles;
using BackgroundWorker.Health;
using BackgroundWorker.UnitTests.CandleSeries;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.UnitTests.Collection;

/// <summary>
/// The live candle worker against a fake exchange: what it asks the exchange for, and what it does with what the exchange tells it.
/// Real time is used for the writer's short batching window, so these wait (briefly) for the effects.
/// </summary>
public class LiveCandleWorkerTests
{
    private const string FuturesBtc1h = "binance_futures_btcusdt_1h";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeLiveCandleStream : ILiveCandleStream
    {
        public List<LiveCandleSubscription> Subscriptions { get; } = [];
        public List<ILiveCandleListener> Listeners { get; } = [];
        public int Disposed;

        public Task<ExchangeResult<IAsyncDisposable>> SubscribeAsync(
            LiveCandleSubscription subscription, ILiveCandleListener listener, CancellationToken cancellationToken)
        {
            Subscriptions.Add(subscription);
            Listeners.Add(listener);
            return Task.FromResult(ExchangeResult<IAsyncDisposable>.Ok(new Handle(this)));
        }

        private sealed class Handle(FakeLiveCandleStream owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Disposed);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingCandleRepository : ICandleRepository
    {
        public List<(string Table, List<Candle> Rows)> Inserts { get; } = [];

        public Task<int> BulkInsert(string tableName, IEnumerable<Candle> candles, CancellationToken cancellationToken)
        {
            lock (Inserts)
                Inserts.Add((tableName, candles.ToList()));
            return Task.FromResult(1);
        }

        public Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Candle>> GetPageAsync(string tableName, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Candle>> GetLatestAsync(string tableName, int limit, DateTime? upTo, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DateTimeOffset?> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleGap>> GetGapsAsync(string tableName, TimeSpan interval, DateTime? expectedStart, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Rig : IAsyncDisposable
    {
        public FakeLiveCandleStream Stream { get; } = new();
        public RecordingCandleRepository Repository { get; } = new();
        public CandleSyncCoordinator Coordinator { get; } = new();
        public WorkerHealthState Health { get; } = new(TimeProvider.System);
        public BinanceLiveCandleWorker Worker { get; private set; } = null!;

        public static async Task<Rig> StartAsync(bool liveCandles = true, params CandleSeriesPlan[] series)
        {
            var rig = new Rig();
            var scopes = new ServiceCollection().AddSingleton<ICandleRepository>(rig.Repository).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            var provider = await CandleSeriesTestSupport.LoadedProviderAsync(TimeProvider.System, series);

            rig.Worker = new BinanceLiveCandleWorker(
                scopes, NullLogger<BinanceLiveCandleWorker>.Instance,
                Options.Create(new LiveCandleSettings { LiveCandles = liveCandles }),
                provider, rig.Stream, rig.Coordinator, rig.Health);

            await rig.Worker.StartAsync(Ct);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
        }
    }

    private static CandleSeriesPlan Futures(SymbolEnum symbol, TimeFrameEnum timeFrame) => TestPlans.Binance(MarketTypeEnum.Futures, symbol, timeFrame);

    private static Candle Candle(DateTime open) => new(open, 1m, 2m, 0.5m, 1.5m, 10m, open.AddHours(1).AddMilliseconds(-1), 15m, 3, 5m, 7m);

    private static readonly DateTime Open = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(5, Ct);
        }
    }

    [Fact]
    public async Task ItSubscribesOncePerMarketAndTimeFrameCoveringAllItsSymbols()
    {
        await using var rig = await Rig.StartAsync(true,
            Futures(SymbolEnum.BTCUSDT, TimeFrameEnum._1H), Futures(SymbolEnum.ETHUSDT, TimeFrameEnum._1H),
            Futures(SymbolEnum.BTCUSDT, TimeFrameEnum._4H));

        await WaitUntilAsync(() => rig.Stream.Subscriptions.Count == 2, "two subscriptions");

        var oneHour = Assert.Single(rig.Stream.Subscriptions, s => s.Timeframe == TimeFrameEnum._1H);
        Assert.Equal((ExchangeEnum.Binance, MarketTypeEnum.Futures), (oneHour.Exchange, oneHour.MarketType));
        Assert.Equal([SymbolEnum.BTCUSDT, SymbolEnum.ETHUSDT], oneHour.Symbols.Order());
        Assert.Equal([SymbolEnum.BTCUSDT], Assert.Single(rig.Stream.Subscriptions, s => s.Timeframe == TimeFrameEnum._4H).Symbols);
    }

    [Fact]
    public async Task ASubscribedStreamIsReportedConnectedAndEveryMessageIsReportedAlive()
    {
        await using var rig = await Rig.StartAsync(true, Futures(SymbolEnum.BTCUSDT, TimeFrameEnum._1H));
        await WaitUntilAsync(() => rig.Stream.Listeners.Count == 1, "subscription");

        Assert.True(rig.Health.Get(HealthComponents.CandleStreamFutures).Connected);

        rig.Stream.Listeners[0].OnMessage();
        rig.Stream.Listeners[0].OnMessage();

        Assert.Equal(2, rig.Health.Get(HealthComponents.CandleStreamFutures).MessageCount);
    }

    [Fact]
    public async Task AClosedCandleIsStoredInItsTableOnceThatTableIsReady()
    {
        await using var rig = await Rig.StartAsync(true, Futures(SymbolEnum.BTCUSDT, TimeFrameEnum._1H));
        await WaitUntilAsync(() => rig.Stream.Listeners.Count == 1, "subscription");
        rig.Coordinator.MarkReady(FuturesBtc1h);
        var candle = Candle(Open);

        rig.Stream.Listeners[0].OnClosedCandle(new Domain.ValueObjects.CandleSeries(ExchangeEnum.Binance, MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H), candle);

        await WaitUntilAsync(() => rig.Repository.Inserts.Count == 1, "the candle being stored");
        var (table, rows) = rig.Repository.Inserts[0];
        Assert.Equal(FuturesBtc1h, table);
        Assert.Same(candle, Assert.Single(rows));
    }

    [Fact]
    public async Task ALiveCandleIsHeldBackWhileItsTableHasNotFinishedTheFirstRestSync()
    {
        // A live candle in a table that is not caught up would become max(open_time) and hide the history before it.
        await using var rig = await Rig.StartAsync(true, Futures(SymbolEnum.BTCUSDT, TimeFrameEnum._1H));
        await WaitUntilAsync(() => rig.Stream.Listeners.Count == 1, "subscription");

        rig.Stream.Listeners[0].OnClosedCandle(new Domain.ValueObjects.CandleSeries(ExchangeEnum.Binance, MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H), Candle(Open));
        await Task.Delay(400, Ct);   // far longer than the writer's batching window

        Assert.Empty(rig.Repository.Inserts);
    }

    [Fact]
    public async Task ACandleOfASymbolNobodyAskedForIsIgnored()
    {
        await using var rig = await Rig.StartAsync(true, Futures(SymbolEnum.BTCUSDT, TimeFrameEnum._1H));
        await WaitUntilAsync(() => rig.Stream.Listeners.Count == 1, "subscription");
        rig.Coordinator.MarkReady(FuturesBtc1h);

        rig.Stream.Listeners[0].OnClosedCandle(new Domain.ValueObjects.CandleSeries(ExchangeEnum.Binance, MarketTypeEnum.Futures, SymbolEnum.ETHUSDT, TimeFrameEnum._1H), Candle(Open));
        await Task.Delay(400, Ct);

        Assert.Empty(rig.Repository.Inserts);
    }

    [Fact]
    public async Task ALostConnectionIsReportedAndARestoredOneAsksTheRestLoopsToCatchUp()
    {
        await using var rig = await Rig.StartAsync(true, Futures(SymbolEnum.BTCUSDT, TimeFrameEnum._1H));
        await WaitUntilAsync(() => rig.Stream.Listeners.Count == 1, "subscription");
        var resyncRequests = rig.Coordinator.Register();

        rig.Stream.Listeners[0].OnConnectionLost();
        Assert.False(rig.Health.Get(HealthComponents.CandleStreamFutures).Connected);
        Assert.False(resyncRequests.TryRead(out _));   // being offline alone asks for nothing

        rig.Stream.Listeners[0].OnConnectionRestored(TimeSpan.FromSeconds(42));

        Assert.True(rig.Health.Get(HealthComponents.CandleStreamFutures).Connected);
        Assert.True(resyncRequests.TryRead(out _), "whatever arrived while offline was missed: the REST loops must repair it now");
    }

    [Fact]
    public async Task StoppingTheWorkerUnsubscribesEveryStream()
    {
        var rig = await Rig.StartAsync(true, Futures(SymbolEnum.BTCUSDT, TimeFrameEnum._1H), Futures(SymbolEnum.BTCUSDT, TimeFrameEnum._4H));
        await WaitUntilAsync(() => rig.Stream.Subscriptions.Count == 2, "subscriptions");

        await rig.DisposeAsync();

        Assert.Equal(2, rig.Stream.Disposed);
    }

    [Fact]
    public async Task WithLiveCandlesSwitchedOffItNeverSubscribes()
    {
        await using var rig = await Rig.StartAsync(false, Futures(SymbolEnum.BTCUSDT, TimeFrameEnum._1H));
        await Task.Delay(200, Ct);

        Assert.Empty(rig.Stream.Subscriptions);
    }
}
