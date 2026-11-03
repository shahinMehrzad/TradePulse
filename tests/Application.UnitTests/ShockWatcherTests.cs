using Application.Exchange;
using Application.Research.Watch;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Application.UnitTests.ShockTape;

namespace Application.UnitTests;

public class ShockWatcherTests
{
    private static readonly ExchangeSymbol Btc = new("BTCUSDT");
    private static readonly WatchedSymbol[] Symbols = [new(ExchangeEnum.Binance, MarketTypeEnum.Futures, 7, Btc)];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Start));
    private readonly FakeLiveTradeStream _stream = new();
    private readonly FakeShockAlertStore _store = new();
    private readonly FakeCandleRepository _candles = new();
    private readonly RecordingNotifier _telegram = new("telegram");
    private readonly RecordingNotifier _email = new("email");
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

    // A symbol normally has the last hour of 1 minute candles stored (the collector keeps them), so the watch starts from them.
    public ShockWatcherTests() => _candles.Stored.AddRange(MinuteCandles());

    private ShockWatcher NewWatcher() => new(_stream, _store, _candles, [_telegram, _email], Options, _time, NullLogger<ShockWatcher>.Instance);

    private Task Run() => Task.Run(() => NewWatcher().RunAsync(Symbols, _stop.Token), Ct);

    private async Task StopAsync(Task run)
    {
        _stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    /// <summary>The watcher works on other threads, so a test waits for what it expects instead of for a fixed time.</summary>
    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Timed out waiting for: {what}");
            await Task.Delay(10, Ct);
        }
    }

    private async Task<ILiveTradeListener> SubscribedAsync()
    {
        await WaitForAsync(() => _stream.Listener is not null, "the subscription");
        return _stream.Listener!;
    }

    private void Quiet(ILiveTradeListener listener, int from, int to, Func<int, decimal>? price = null)
    {
        for (var second = from; second < to; second++)
            listener.OnTrade(Btc, Trade(second, (price ?? Wobble)(second)));
    }

    /// <summary>Moves the fake clock by seconds, giving the watcher's tasks a moment after each so every tick is seen.</summary>
    private async Task PassAsync(int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15, Ct);
        }
    }

    [Fact]
    public async Task AShockIsToldToEveryNotifierAndStored()
    {
        var run = Run();
        var listener = await SubscribedAsync();

        Quiet(listener, 0, 200, second => Price(second, at: 150, jump: 0.01m));
        await WaitForAsync(() => _telegram.Sent.Count > 0 && _email.Sent.Count > 0 && _store.Saved.Count > 0, "the alert");

        var message = Assert.Single(_telegram.Sent);
        Assert.StartsWith("ALARM: BTCUSDT rose 1.", message.Title);   // the jump is 1 %, plus a little wobble
        Assert.EndsWith("% in 10 s", message.Title);
        Assert.Equal(message, Assert.Single(_email.Sent));
        var saved = Assert.Single(_store.Saved);
        Assert.Equal((7, AlertLevelEnum.Alarm, EventDirectionEnum.Up), (saved.SymbolListingId, saved.Level, saved.Direction));
        await StopAsync(run);
    }

    [Fact]
    public async Task ANotifierThatFailsDoesNotKeepTheOthersOrTheStoreFromTheAlert()
    {
        _telegram.Throws = true;
        var run = Run();
        var listener = await SubscribedAsync();

        Quiet(listener, 0, 200, second => Price(second, at: 150, jump: 0.01m));
        await WaitForAsync(() => _email.Sent.Count > 0 && _store.Saved.Count > 0, "the alert");

        Assert.Empty(_telegram.Sent);
        await StopAsync(run);
    }

    [Fact]
    public async Task AStoreThatFailsDoesNotStopTheWatch()
    {
        _store.Throws = true;
        var run = Run();
        var listener = await SubscribedAsync();

        Quiet(listener, 0, 200, second => Price(second, at: 150, jump: 0.01m));
        await WaitForAsync(() => _telegram.Sent.Count > 0, "the first alert");

        // The watch still judges: a fall later is told as well.
        Quiet(listener, 200, 600, second => Price(second, at: 150, jump: 0.01m) * (second >= 560 ? 0.98m : 1m));
        await WaitForAsync(() => _telegram.Sent.Any(m => m.Title.Contains("fell")), "the second alert");
        await StopAsync(run);
    }

    [Fact]
    public async Task AQuietMarketSendsNothing()
    {
        var run = Run();
        var listener = await SubscribedAsync();

        Quiet(listener, 0, 300);
        await PassAsync(3);

        Assert.Empty(_telegram.Sent);
        Assert.Empty(_store.Saved);
        await StopAsync(run);
    }

    [Fact]
    public async Task ALostConnectionIsToldAsBlindOnceAndAsRestoredWhenItIsBack()
    {
        var run = Run();
        var listener = await SubscribedAsync();

        listener.OnConnectionLost();
        await PassAsync(10);
        Assert.Empty(_telegram.Sent);                      // a short drop is not worth a message

        await PassAsync(10);
        await WaitForAsync(() => _telegram.Sent.Count == 1, "the blind message");
        await PassAsync(10);
        Assert.Equal("Watch is blind", Assert.Single(_telegram.Sent).Title);   // once, not every second

        listener.OnConnectionRestored(TimeSpan.FromSeconds(30));
        await PassAsync(2);
        await WaitForAsync(() => _telegram.Sent.Count == 2, "the restored message");
        Assert.Equal("Watch sees the market again", _telegram.Sent[1].Title);
        await StopAsync(run);
    }

    [Fact]
    public async Task AShortDropThatIsBackBeforeTheBlindTimeIsNotMentioned()
    {
        var run = Run();
        var listener = await SubscribedAsync();

        listener.OnConnectionLost();
        await PassAsync(5);
        listener.OnConnectionRestored(TimeSpan.FromSeconds(5));
        await PassAsync(20);

        Assert.Empty(_telegram.Sent);
        await StopAsync(run);
    }

    [Fact]
    public async Task AFailedSubscriptionIsRetriedAndTheAdminIsToldTheWatchIsBlind()
    {
        _stream.Answers.Enqueue(ExchangeResult<IAsyncDisposable>.Fail("the exchange said no"));
        _stream.Answers.Enqueue(ExchangeResult<IAsyncDisposable>.Fail("the exchange said no"));
        var run = Run();

        await WaitForAsync(() => _stream.SubscribeCalls == 1, "the first attempt");
        await PassAsync(30);                                 // retry after 30 s fails again, 30 s offline: blind
        await WaitForAsync(() => _stream.SubscribeCalls == 2, "the second attempt");
        await WaitForAsync(() => _telegram.Sent.Count == 1, "the blind message");
        Assert.Equal("Watch is blind", _telegram.Sent[0].Title);

        await PassAsync(30);                                 // the third attempt works
        await WaitForAsync(() => _stream.Listener is not null, "the subscription");
        await WaitForAsync(() => _telegram.Sent.Count == 2, "the restored message");
        Assert.Equal("Watch sees the market again", _telegram.Sent[1].Title);
        await StopAsync(run);
    }

    [Fact]
    public async Task StoppingUnsubscribesAndEnds()
    {
        var run = Run();
        await SubscribedAsync();

        await StopAsync(run);

        Assert.True(_stream.Unsubscribed);
    }

    [Fact]
    public async Task TradesOfASymbolNotWatchedAreIgnored()
    {
        var run = Run();
        var listener = await SubscribedAsync();

        Quiet(listener, 0, 5);
        listener.OnTrade(new ExchangeSymbol("ETHUSDT"), Trade(5, 3000m));
        await PassAsync(2);

        Assert.Empty(_telegram.Sent);
        await StopAsync(run);
    }

    private static bool IsWarmingUp(Application.Notifications.Alert message) => message.Title.StartsWith("Watch is warming up");

    [Fact]
    public async Task WithMinuteCandlesStoredItJudgesFromTheFirstSecondsNotAfterAnHour()
    {
        var run = Run();
        var listener = await SubscribedAsync();

        // 30 seconds after the start the price jumps 1 %: the baseline is still the estimate from the candles.
        Quiet(listener, 0, 60, second => Price(second, at: 30, jump: 0.01m));
        await WaitForAsync(() => _telegram.Sent.Count > 0, "the alert");

        Assert.StartsWith("ALARM: BTCUSDT rose", Assert.Single(_telegram.Sent).Title);
        Assert.Equal("binance_futures_btcusdt_1m", _candles.LastRead!.Value.TableName);
        await StopAsync(run);
    }

    [Fact]
    public async Task WithoutMinuteCandlesTheSameJumpIsNotJudgedUntilTheBaselineIsFullAndTheAdminIsToldSo()
    {
        _candles.Stored.Clear();
        var run = Run();
        var listener = await SubscribedAsync();

        Quiet(listener, 0, 60, second => Price(second, at: 30, jump: 0.01m));
        await WaitForAsync(() => _telegram.Sent.Count > 0, "the warm-up message");
        await PassAsync(3);

        var message = Assert.Single(_telegram.Sent);
        Assert.Equal("Watch is warming up: BTCUSDT", message.Title);
        Assert.Contains("no recent 1 minute candles", message.Body, StringComparison.OrdinalIgnoreCase);
        await StopAsync(run);
    }

    [Fact]
    public async Task StaleMinuteCandlesAreNotUsedBecauseTheyDescribeAnotherTime()
    {
        _candles.Stored.Clear();
        _candles.Stored.AddRange(MinuteCandles(endsMinutesBefore: 120));
        var run = Run();
        var listener = await SubscribedAsync();

        Quiet(listener, 0, 60, second => Price(second, at: 30, jump: 0.01m));
        await WaitForAsync(() => _telegram.Sent.Count > 0, "the warm-up message");
        await PassAsync(3);

        Assert.True(IsWarmingUp(Assert.Single(_telegram.Sent)), "the jump was not judged, only the warm-up was told");
        await StopAsync(run);
    }

    [Fact]
    public async Task ACandleTableThatCannotBeReadDoesNotStopTheWatchAndTheAdminIsToldItIsWarmingUp()
    {
        _candles.PerTable["some_other_table"] = [];   // the 1 minute table does not exist: the repository throws
        var run = Run();

        var listener = await SubscribedAsync();

        Quiet(listener, 0, 200, second => Price(second, at: 150, jump: 0.01m));
        await WaitForAsync(() => _telegram.Sent.Any(m => m.Title.StartsWith("ALARM")), "the alert");
        Assert.Single(_telegram.Sent, IsWarmingUp);
        await StopAsync(run);
    }

    [Fact]
    public async Task TheWarmUpIsToldOnceEvenWhenTheSubscriptionHadToBeTriedAgain()
    {
        _candles.Stored.Clear();
        _stream.Answers.Enqueue(ExchangeResult<IAsyncDisposable>.Fail("the exchange said no"));
        var run = Run();
        await WaitForAsync(() => _stream.SubscribeCalls == 1, "the first attempt");
        Assert.DoesNotContain(_telegram.Sent, IsWarmingUp);   // nothing is watched yet: it would be told for a symbol that is not followed

        await PassAsync(30);
        await SubscribedAsync();
        await WaitForAsync(() => _telegram.Sent.Any(IsWarmingUp), "the warm-up message");
        await PassAsync(65);

        Assert.Single(_telegram.Sent, IsWarmingUp);
        await StopAsync(run);
    }
}
