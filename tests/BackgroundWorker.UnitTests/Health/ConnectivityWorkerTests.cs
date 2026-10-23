using Application.Exchange;
using BackgroundWorker.Health;
using BackgroundWorker.UnitTests.CandleSeries;
using Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.UnitTests.Health;

/// <summary>The connection probe against a fake exchange: what it pings, and what it reports about the answers.</summary>
public class ConnectivityWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeProbe : IExchangeProbe
    {
        public List<MarketTypeEnum> Pings { get; } = [];
        public Func<MarketTypeEnum, ExchangeResult<TimeSpan>> PingAnswer { get; set; } = _ => ExchangeResult<TimeSpan>.Ok(TimeSpan.FromMilliseconds(12));
        public List<SymbolEnum> StreamSymbols { get; } = [];
        public List<IProbeStreamListener> Listeners { get; } = [];
        public int Disposed;

        public Task<ExchangeResult<TimeSpan>> PingAsync(ExchangeEnum exchange, MarketTypeEnum market, CancellationToken cancellationToken)
        {
            lock (Pings)
                Pings.Add(market);
            return Task.FromResult(PingAnswer(market));
        }

        public Task<ExchangeResult<IAsyncDisposable>> SubscribeToProbeStreamAsync(
            ExchangeEnum exchange, SymbolEnum symbol, IProbeStreamListener listener, CancellationToken cancellationToken)
        {
            StreamSymbols.Add(symbol);
            Listeners.Add(listener);
            return Task.FromResult(ExchangeResult<IAsyncDisposable>.Ok(new Handle(this)));
        }

        private sealed class Handle(FakeProbe owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                Interlocked.Increment(ref owner.Disposed);
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public FakeProbe Probe { get; } = new();
        public WorkerHealthState Health { get; } = new(TimeProvider.System);
        public BinanceConnectivityWorker Worker { get; private set; } = null!;

        public static async Task<Rig> StartAsync(
            MarketTypeEnum[] markets, SymbolEnum probeSymbol = SymbolEnum.BTCUSDT, Func<MarketTypeEnum, ExchangeResult<TimeSpan>>? pingAnswer = null)
        {
            var rig = new Rig();
            if (pingAnswer is not null)
                rig.Probe.PingAnswer = pingAnswer;

            var provider = await CandleSeriesTestSupport.LoadedProviderAsync(
                TimeProvider.System, markets.Select(m => TestPlans.Binance(m, SymbolEnum.BTCUSDT, TimeFrameEnum._1H)).ToArray());

            rig.Worker = new BinanceConnectivityWorker(
                NullLogger<BinanceConnectivityWorker>.Instance, rig.Health, rig.Probe,
                Options.Create(new HealthConfigurations { ProbeSymbol = probeSymbol }), provider);

            await rig.Worker.StartAsync(Ct);
            return rig;
        }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
        }
    }

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
    public async Task ItPingsFuturesRightAwayAndReportsTheSuccess()
    {
        await using var rig = await Rig.StartAsync([MarketTypeEnum.Futures]);

        await WaitUntilAsync(() => rig.Health.Get(HealthComponents.FuturesRest).LastSuccessAt is not null, "the first ping");

        Assert.Equal([MarketTypeEnum.Futures], rig.Probe.Pings);
        Assert.Equal(0, rig.Health.Get(HealthComponents.FuturesRest).ConsecutiveFailures);
    }

    [Fact]
    public async Task SpotIsOnlyPingedWhileASpotSeriesIsCollected()
    {
        await using var futuresOnly = await Rig.StartAsync([MarketTypeEnum.Futures]);
        await using var withSpot = await Rig.StartAsync([MarketTypeEnum.Futures, MarketTypeEnum.Spot]);

        await WaitUntilAsync(() => withSpot.Health.Get(HealthComponents.SpotRest).LastSuccessAt is not null, "the spot ping");
        await WaitUntilAsync(() => futuresOnly.Health.Get(HealthComponents.FuturesRest).LastSuccessAt is not null, "the futures ping");

        Assert.DoesNotContain(MarketTypeEnum.Spot, futuresOnly.Probe.Pings);
        Assert.Null(futuresOnly.Health.Get(HealthComponents.SpotRest).LastSuccessAt);
    }
    [Fact]
    public async Task AFailedPingIsReportedWithTheExchangesReason()
    {
        await using var rig = await Rig.StartAsync([MarketTypeEnum.Futures], pingAnswer: _ => ExchangeResult<TimeSpan>.Fail("503 service unavailable"));

        await WaitUntilAsync(() => rig.Health.Get(HealthComponents.FuturesRest).ConsecutiveFailures == 1, "the failure being reported");

        Assert.Equal("503 service unavailable", rig.Health.Get(HealthComponents.FuturesRest).LastError);
    }

    [Fact]
    public async Task APingThatThrowsIsAFailureNotACrash()
    {
        // An exception out of a worker would stop the whole host.
        await using var rig = await Rig.StartAsync([MarketTypeEnum.Futures], pingAnswer: _ => throw new InvalidOperationException("socket exploded"));

        await WaitUntilAsync(() => rig.Health.Get(HealthComponents.FuturesRest).ConsecutiveFailures == 1, "the failure being reported");

        Assert.Equal("socket exploded", rig.Health.Get(HealthComponents.FuturesRest).LastError);
    }

    [Fact]
    public async Task TheProbeStreamIsTheConfiguredSymbolAndItsMessagesAndReconnectsAreReported()
    {
        await using var rig = await Rig.StartAsync([MarketTypeEnum.Futures], SymbolEnum.ETHUSDT);
        await WaitUntilAsync(() => rig.Probe.Listeners.Count == 1, "the subscription");
        var listener = rig.Probe.Listeners[0];

        Assert.Equal([SymbolEnum.ETHUSDT], rig.Probe.StreamSymbols);
        Assert.True(rig.Health.Get(HealthComponents.FuturesWebSocket).Connected);

        listener.OnMessage();
        listener.OnMessage();
        Assert.Equal(2, rig.Health.Get(HealthComponents.FuturesWebSocket).MessageCount);

        listener.OnConnectionLost();
        Assert.False(rig.Health.Get(HealthComponents.FuturesWebSocket).Connected);

        listener.OnConnectionRestored(TimeSpan.FromSeconds(3));
        Assert.True(rig.Health.Get(HealthComponents.FuturesWebSocket).Connected);
    }

    [Fact]
    public async Task StoppingTheWorkerUnsubscribesTheProbeStream()
    {
        var rig = await Rig.StartAsync([MarketTypeEnum.Futures]);
        await WaitUntilAsync(() => rig.Probe.Listeners.Count == 1, "the subscription");

        await rig.DisposeAsync();

        Assert.Equal(1, rig.Probe.Disposed);
    }
}
