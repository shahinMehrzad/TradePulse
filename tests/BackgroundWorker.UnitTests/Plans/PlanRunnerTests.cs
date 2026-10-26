using BackgroundWorker.CandleSeries;
using BackgroundWorker.Plans;
using BackgroundWorker.UnitTests.CandleSeries;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Plans;

/// <summary>
/// How a worker follows the admin's choice: it is started for what concerns it, restarted when that changes, left alone when
/// something else changes, and stopped when nothing is left. The provider is the real one on a fake clock and a fake database.
/// </summary>
public class PlanRunnerTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CandleSeriesPlan Btc1hFutures = TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);
    private static readonly CandleSeriesPlan Eth1hFutures = TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.ETHUSDT, TimeFrameEnum._1H);
    private static readonly CandleSeriesPlan Btc1hSpot = TestPlans.Binance(MarketTypeEnum.Spot, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeCandleSeriesSource _source = new();
    private readonly List<string> _events = [];
    private readonly CancellationTokenSource _shutdown = new();
    private CandleSeriesProvider _provider = null!;

    public ValueTask InitializeAsync()
    {
        _provider = CandleSeriesTestSupport.NewProvider(_source, _time);
        return ValueTask.CompletedTask;
    }

    /// <summary>The database holds these series from the start; the provider reads them at once.</summary>
    private async Task StartProviderWith(params CandleSeriesPlan[] series)
    {
        _source.Series.AddRange(series);
        await _provider.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();
        await _provider.StopAsync(CancellationToken.None);
        _provider.Dispose();
        _shutdown.Dispose();
    }

    private void Record(string text)
    {
        lock (_events)
            _events.Add(text);
    }

    private string[] Events()
    {
        lock (_events)
            return [.. _events];
    }

    /// <summary>Starts the runner for the futures series; the collection it runs notes when it starts and stops, then waits to be cancelled.</summary>
    private Task StartRunner(Func<IReadOnlyList<CandleSeriesPlan>, IReadOnlyList<CandleSeriesPlan>>? select = null, Func<IReadOnlyList<CandleSeriesPlan>, CancellationToken, Task>? run = null) =>
        PlanRunner.RunAsync(_provider,
            select ?? (plans => plans.Where(p => p.Series.MarketType == MarketTypeEnum.Futures).ToList()),
            run ?? (async (items, ct) =>
            {
                Record($"start {string.Join("+", items.Select(i => i.Series.Symbol).Order())}");
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    Record("stop");
                }
            }),
            NullLogger.Instance, "candle series", _shutdown.Token);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(5, Ct);
        }
    }

    /// <summary>The provider sets its status before it waits for the next look, so one jump of the clock can be lost: keep moving it.</summary>
    private async Task ChangeTheChoiceAsync(Action change, long toVersion)
    {
        change();
        await WaitUntilAsync(() =>
        {
            _time.Advance(CandleSeriesProvider.RefreshInterval);
            return _provider.Version >= toVersion;
        }, $"the provider to publish version {toVersion}");
    }

    [Fact]
    public async Task NothingRunsWhileNothingIsChosenAndTheCollectionStartsWhenSomethingIs()
    {
        await StartProviderWith();
        var runner = StartRunner();
        await WaitUntilAsync(() => _provider.Status == PlanStatus.WaitingForConfiguration, "an empty table");

        Assert.Empty(Events());

        await ChangeTheChoiceAsync(() => _source.Series.Add(Btc1hFutures), toVersion: 2);
        await WaitUntilAsync(() => Events().Length == 1, "the collection to start");

        Assert.Equal(["start BTCUSDT"], Events());
        Assert.False(runner.IsCompleted);
    }

    [Fact]
    public async Task AChangeInWhatConcernsTheWorkerStopsTheCollectionAndStartsItAgainWithTheNewList()
    {
        await StartProviderWith(Btc1hFutures);
        _ = StartRunner();
        await WaitUntilAsync(() => Events().Length == 1, "the first collection");

        await ChangeTheChoiceAsync(() => _source.Series.Add(Eth1hFutures), toVersion: 2);
        await WaitUntilAsync(() => Events().Length == 3, "the restart");

        // The old collection has let go before the new one starts (two would otherwise hold the same connections).
        Assert.Equal(["start BTCUSDT", "stop", "start BTCUSDT+ETHUSDT"], Events());
    }

    [Fact]
    public async Task AChangeThatDoesNotConcernTheWorkerLeavesItAlone()
    {
        await StartProviderWith(Btc1hFutures);
        _ = StartRunner();
        await WaitUntilAsync(() => Events().Length == 1, "the first collection");

        await ChangeTheChoiceAsync(() => _source.Series.Add(Btc1hSpot), toVersion: 2);   // a spot series: not what this worker takes
        await Task.Delay(100, Ct);

        Assert.Equal(["start BTCUSDT"], Events());
    }

    [Fact]
    public async Task RemovingEverythingThisWorkerTookStopsItAndAddingSomethingStartsItAgain()
    {
        await StartProviderWith(Btc1hFutures);
        _ = StartRunner();
        await WaitUntilAsync(() => Events().Length == 1, "the first collection");

        await ChangeTheChoiceAsync(() => _source.Series.Clear(), toVersion: 2);
        await WaitUntilAsync(() => Events().Length == 2, "the stop");
        Assert.Equal(["start BTCUSDT", "stop"], Events());

        await ChangeTheChoiceAsync(() => _source.Series.Add(Eth1hFutures), toVersion: 3);
        await WaitUntilAsync(() => Events().Length == 3, "the new start");
        Assert.Equal("start ETHUSDT", Events()[^1]);
    }

    [Fact]
    public async Task ACollectionThatEndsByItselfIsNotStartedAgainForTheSameChoice()
    {
        await StartProviderWith(Btc1hFutures);
        var runner = StartRunner(run: (items, _) =>
        {
            Record("start");
            return Task.CompletedTask;
        });
        await WaitUntilAsync(() => Events().Length == 1, "the collection");

        await ChangeTheChoiceAsync(() => _source.Series.Add(Btc1hSpot), toVersion: 2);   // unrelated
        await Task.Delay(100, Ct);

        Assert.Equal(["start"], Events());
        Assert.False(runner.IsCompleted);   // the worker itself keeps following the choice
    }

    [Fact]
    public async Task ACollectionThatFailsEndsTheWorkerWithItsException()
    {
        await StartProviderWith(Btc1hFutures);
        var runner = StartRunner(run: (_, _) => Task.FromException(new InvalidOperationException("boom")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.WaitAsync(TimeSpan.FromSeconds(5), Ct));

        Assert.Equal("boom", error.Message);
    }

    [Fact]
    public async Task ShuttingDownStopsTheCollectionAndEndsTheWorkerQuietly()
    {
        await StartProviderWith(Btc1hFutures);
        var runner = StartRunner();
        await WaitUntilAsync(() => Events().Length == 1, "the collection");

        await _shutdown.CancelAsync();
        await runner.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(["start BTCUSDT", "stop"], Events());
    }
}
