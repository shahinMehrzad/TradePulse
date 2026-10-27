using Application.Exchange;
using BackgroundWorker.Collection.Liquidations;
using BackgroundWorker.Health;
using BackgroundWorker.SymbolData;
using BackgroundWorker.UnitTests.SymbolData;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackgroundWorker.UnitTests.Collection;

/// <summary>
/// The liquidation worker against a fake exchange: what it subscribes to, and that what the exchange sends ends up stored,
/// also when the worker is stopped with liquidations still queued. Real time is used for the writer's batching window.
/// </summary>
public class LiquidationWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeLiquidationStream : ILiquidationStream
    {
        public List<IReadOnlyList<SymbolEnum>> Subscriptions { get; } = [];
        public List<ILiquidationListener> Listeners { get; } = [];
        public int Disposed;

        public Task<ExchangeResult<IAsyncDisposable>> SubscribeAsync(
            ExchangeEnum exchange, IReadOnlyList<SymbolEnum> symbols, ILiquidationListener listener, CancellationToken cancellationToken)
        {
            Subscriptions.Add(symbols);
            Listeners.Add(listener);
            return Task.FromResult(ExchangeResult<IAsyncDisposable>.Ok(new Handle(this)));
        }

        private sealed class Handle(FakeLiquidationStream owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Disposed);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class RecordingRepository : IMarketDataRepository<Liquidation>
    {
        public List<string> Created { get; } = [];
        public List<(string Table, List<Liquidation> Rows)> Inserts { get; } = [];

        public Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken)
        {
            lock (Created)
                Created.Add(tableName);
            return Task.CompletedTask;
        }

        public Task<int> BulkInsert(string tableName, IEnumerable<Liquidation> rows, CancellationToken cancellationToken)
        {
            lock (Inserts)
            {
                var list = rows.ToList();
                Inserts.Add((tableName, list));
                return Task.FromResult(list.Count);
            }
        }

        public Task<DateTime?> GetLatestTimeAsync(string tableName, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Rig : IAsyncDisposable
    {
        public FakeLiquidationStream Stream { get; } = new();
        public RecordingRepository Repository { get; } = new();
        public WorkerHealthState Health { get; } = new(TimeProvider.System);
        public CollectionStats Stats { get; } = new();
        public BinanceLiquidationWorker Worker { get; private set; } = null!;

        public static async Task<Rig> StartAsync(params SymbolDataPlan[] plans)
        {
            var rig = new Rig();
            var scopes = new ServiceCollection().AddSingleton<IMarketDataRepository<Liquidation>>(rig.Repository)
                .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            var provider = await SymbolDataTestSupport.LoadedProviderAsync(TimeProvider.System, plans);

            rig.Worker = new BinanceLiquidationWorker(scopes, NullLogger<BinanceLiquidationWorker>.Instance, provider, rig.Stream, rig.Health, rig.Stats);
            await rig.Worker.StartAsync(Ct);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
        }
    }

    private static Liquidation Sample(decimal price) =>
        new(new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), OrderSideEnum.Sell, "LIMIT", "IOC", "FILLED", 1m, price, price, 1m, 1m);

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
    public async Task ItCreatesTheTablesAndSubscribesToTheSymbolsThatAskedForLiquidationsOnly()
    {
        await using var rig = await Rig.StartAsync(
            SymbolDataTestSupport.Binance(SymbolEnum.BTCUSDT, funding: false, liquidation: true),
            SymbolDataTestSupport.Binance(SymbolEnum.ETHUSDT, funding: true, liquidation: false),
            SymbolDataTestSupport.Binance(SymbolEnum.BNBUSDT, funding: true, liquidation: true));

        await WaitUntilAsync(() => rig.Stream.Subscriptions.Count == 1, "the subscription");

        Assert.Equal([SymbolEnum.BTCUSDT, SymbolEnum.BNBUSDT], rig.Stream.Subscriptions[0].Order(Comparer<SymbolEnum>.Create((a, b) => ((int)a).CompareTo((int)b))));
        Assert.Equal(["binance_futures_btcusdt_liquidation", "binance_futures_bnbusdt_liquidation"], rig.Repository.Created.Order(StringComparer.Ordinal).Reverse());
        Assert.True(rig.Health.Get(HealthComponents.LiquidationStream).Connected);
    }

    [Fact]
    public async Task WhenTheAdminChangesTheSymbolsTheStreamIsClosedAndOpenedAgainForTheNewOnesAndOtherChangesLeaveItAlone()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var source = new FakeSymbolDataSource();
        source.Plans.Add(SymbolDataTestSupport.Binance(SymbolEnum.BTCUSDT, funding: false, liquidation: true));
        using var provider = SymbolDataTestSupport.NewProvider(source, time);
        await provider.StartAsync(Ct);

        var rig = new Rig();
        var scopes = new ServiceCollection().AddSingleton<IMarketDataRepository<Liquidation>>(rig.Repository)
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        using var worker = new BinanceLiquidationWorker(scopes, NullLogger<BinanceLiquidationWorker>.Instance, provider, rig.Stream, rig.Health, rig.Stats);
        await worker.StartAsync(Ct);
        await WaitUntilAsync(() => rig.Stream.Subscriptions.Count == 1, "the first subscription");

        // The provider sets its status before it waits for the next look, so one jump of the clock can be lost: keep moving it.
        async Task WaitForVersionAsync(long version) => await WaitUntilAsync(() =>
        {
            time.Advance(SymbolDataProvider.RefreshInterval);
            return provider.Version >= version;
        }, $"the provider to publish version {version}");

        // A symbol is added: the old subscription is closed first, then one for both symbols is opened.
        source.Plans.Add(SymbolDataTestSupport.Binance(SymbolEnum.ETHUSDT, funding: false, liquidation: true));
        await WaitForVersionAsync(2);
        await WaitUntilAsync(() => rig.Stream.Subscriptions.Count == 2, "the second subscription");

        Assert.Equal(1, rig.Stream.Disposed);
        Assert.Equal([SymbolEnum.BTCUSDT, SymbolEnum.ETHUSDT], rig.Stream.Subscriptions[1].Order());
        Assert.Contains("binance_futures_ethusdt_liquidation", rig.Repository.Created);

        // A symbol that only wants its funding rate is no concern of this worker.
        source.Plans.Add(SymbolDataTestSupport.Binance(SymbolEnum.BNBUSDT, funding: true, liquidation: false));
        await WaitForVersionAsync(3);
        await Task.Delay(100, Ct);

        Assert.Equal((2, 1), (rig.Stream.Subscriptions.Count, rig.Stream.Disposed));

        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(2, rig.Stream.Disposed);   // and shutting down closes the last one
    }

    [Fact]
    public async Task ALiquidationIsStoredInTheTableOfItsSymbolAndCountsAsAMessage()
    {
        await using var rig = await Rig.StartAsync(
            SymbolDataTestSupport.Binance(SymbolEnum.BTCUSDT, funding: false, liquidation: true),
            SymbolDataTestSupport.Binance(SymbolEnum.ETHUSDT, funding: false, liquidation: true));
        await WaitUntilAsync(() => rig.Stream.Listeners.Count == 1, "the subscription");
        var listener = rig.Stream.Listeners[0];

        listener.OnLiquidation(SymbolEnum.BTCUSDT, Sample(100m));
        listener.OnLiquidation(SymbolEnum.ETHUSDT, Sample(200m));
        listener.OnLiquidation(SymbolEnum.BTCUSDT, Sample(101m));

        await WaitUntilAsync(() => rig.Repository.Inserts.Sum(i => i.Rows.Count) == 3, "all three being stored");
        var btc = rig.Repository.Inserts.Where(i => i.Table == "binance_futures_btcusdt_liquidation").SelectMany(i => i.Rows).Select(r => r.Price);
        Assert.Equal([100m, 101m], btc);   // order kept, and gathered per table
        Assert.Equal([200m], rig.Repository.Inserts.Where(i => i.Table == "binance_futures_ethusdt_liquidation").SelectMany(i => i.Rows).Select(r => r.Price));
        Assert.Equal(3, rig.Health.Get(HealthComponents.LiquidationStream).MessageCount);
        Assert.Equal(3, rig.Stats.TakeSinceLastCall().Liquidations);
    }

    [Fact]
    public async Task ALostConnectionIsReportedUntilTheLibraryRestoresIt()
    {
        await using var rig = await Rig.StartAsync(SymbolDataTestSupport.Binance(funding: false, liquidation: true));
        await WaitUntilAsync(() => rig.Stream.Listeners.Count == 1, "the subscription");

        rig.Stream.Listeners[0].OnConnectionLost();
        Assert.False(rig.Health.Get(HealthComponents.LiquidationStream).Connected);

        rig.Stream.Listeners[0].OnConnectionRestored(TimeSpan.FromSeconds(5));
        Assert.True(rig.Health.Get(HealthComponents.LiquidationStream).Connected);
    }

    [Fact]
    public async Task LiquidationsStillQueuedWhenTheWorkerStopsAreStoredNotLost()
    {
        var rig = await Rig.StartAsync(SymbolDataTestSupport.Binance(funding: false, liquidation: true));
        await WaitUntilAsync(() => rig.Stream.Listeners.Count == 1, "the subscription");

        rig.Stream.Listeners[0].OnLiquidation(SymbolEnum.BTCUSDT, Sample(100m));
        await rig.DisposeAsync();   // stops long before the writer's batching window is over

        Assert.Equal([100m], rig.Repository.Inserts.SelectMany(i => i.Rows).Select(r => r.Price));
        Assert.Equal(1, rig.Stream.Disposed);   // and the stream was unsubscribed
        Assert.Equal(1, rig.Stats.TakeSinceLastCall().Liquidations);
    }

    [Fact]
    public async Task WhenNoSymbolAsksForLiquidationsItNeverSubscribes()
    {
        await using var rig = await Rig.StartAsync(SymbolDataTestSupport.Binance(funding: true, liquidation: false));
        await Task.Delay(200, Ct);

        Assert.Empty(rig.Stream.Subscriptions);
        Assert.Empty(rig.Repository.Created);
    }
}
