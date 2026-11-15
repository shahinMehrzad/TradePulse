using Application.Exchange;
using BackgroundWorker.Health;
using BackgroundWorker.UnitTests.CandleSeries;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.UnitTests.Health;

/// <summary>The Pepperstone connection probe against a fake exchange: when it asks, and what it reports about the answers.</summary>
public class CtraderConnectivityWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeProbe : IExchangeProbe
    {
        public bool Served { get; set; } = true;
        public Func<ExchangeResult<TimeSpan>> Answer { get; set; } = () => ExchangeResult<TimeSpan>.Ok(TimeSpan.FromMilliseconds(40));
        public List<(ExchangeEnum Exchange, MarketTypeEnum Market)> Pings { get; } = [];

        public bool Serves(ExchangeEnum exchange) => Served;

        public Task<ExchangeResult<TimeSpan>> PingAsync(ExchangeEnum exchange, MarketTypeEnum market, CancellationToken cancellationToken)
        {
            lock (Pings)
                Pings.Add((exchange, market));
            return Task.FromResult(Answer());
        }

        public Task<ExchangeResult<IAsyncDisposable>> SubscribeToProbeStreamAsync(
            ExchangeEnum exchange, ExchangeSymbol symbol, IProbeStreamListener listener, CancellationToken cancellationToken) =>
            throw new NotSupportedException("the Pepperstone probe has no stream");
    }

    private sealed class Rig : IAsyncDisposable
    {
        public FakeProbe Probe { get; } = new();
        public WorkerHealthState Health { get; } = new(TimeProvider.System);
        private CtraderConnectivityWorker Worker { get; set; } = null!;

        public static async Task<Rig> StartAsync(bool pepperstoneSeries, Action<FakeProbe>? setUp = null)
        {
            var rig = new Rig();
            setUp?.Invoke(rig.Probe);

            var provider = await CandleSeriesTestSupport.LoadedProviderAsync(TimeProvider.System,
                pepperstoneSeries
                    ? [TestPlans.Pepperstone(), TestPlans.Binance(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H)]
                    : [TestPlans.Binance(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H)]);

            rig.Worker = new CtraderConnectivityWorker(
                NullLogger<CtraderConnectivityWorker>.Instance, rig.Health, rig.Probe, Options.Create(new HealthConfigurations()), provider);
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
    public async Task ItAsksPepperstoneRightAwayAndReportsTheSuccess()
    {
        await using var rig = await Rig.StartAsync(pepperstoneSeries: true);

        await WaitUntilAsync(() => rig.Health.Get(HealthComponents.CtraderConnection).LastSuccessAt is not null, "the first probe");

        Assert.Equal([(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd)], rig.Probe.Pings);
        Assert.Equal(0, rig.Health.Get(HealthComponents.CtraderConnection).ConsecutiveFailures);
    }

    [Fact]
    public async Task WithNoPepperstoneSeriesItNeitherAsksNorReportsAnything()
    {
        await using var rig = await Rig.StartAsync(pepperstoneSeries: false);

        await Task.Delay(300, Ct);

        Assert.Empty(rig.Probe.Pings);
        Assert.Null(rig.Health.Get(HealthComponents.CtraderConnection).LastSuccessAt);
        Assert.Equal(0, rig.Health.Get(HealthComponents.CtraderConnection).ConsecutiveFailures);
    }

    [Fact]
    public async Task AFailedProbeIsReportedWithCTradersReason()
    {
        await using var rig = await Rig.StartAsync(true, p => p.Answer = () => ExchangeResult<TimeSpan>.Fail("The access token was withdrawn or has expired."));

        await WaitUntilAsync(() => rig.Health.Get(HealthComponents.CtraderConnection).ConsecutiveFailures == 1, "the failure being reported");

        Assert.Equal("The access token was withdrawn or has expired.", rig.Health.Get(HealthComponents.CtraderConnection).LastError);
    }

    [Fact]
    public async Task AProbeThatThrowsIsAFailureNotACrash()
    {
        // An exception out of a worker would stop the whole host.
        await using var rig = await Rig.StartAsync(true, p => p.Answer = () => throw new InvalidOperationException("secret-host unreachable"));

        await WaitUntilAsync(() => rig.Health.Get(HealthComponents.CtraderConnection).ConsecutiveFailures == 1, "the failure being reported");

        // Only the kind of exception: its text can hold an address.
        Assert.Equal(nameof(InvalidOperationException), rig.Health.Get(HealthComponents.CtraderConnection).LastError);
    }

    [Fact]
    public async Task APepperstoneSeriesWithoutAConfiguredCTraderIsAFailureNotSilence()
    {
        await using var rig = await Rig.StartAsync(true, p => p.Served = false);

        await WaitUntilAsync(() => rig.Health.Get(HealthComponents.CtraderConnection).ConsecutiveFailures == 1, "the failure being reported");

        Assert.Equal(CtraderConnectivityWorker.NotConfigured, rig.Health.Get(HealthComponents.CtraderConnection).LastError);
        Assert.Empty(rig.Probe.Pings);
    }
}
