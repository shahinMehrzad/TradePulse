using Application.Exchange;
using Application.Research.Watch;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Application.UnitTests.ShockTape;

namespace Application.UnitTests;

/// <summary>The symbols the watch follows change while it runs: the admin switches them on and off.</summary>
public class ShockWatcherListTests
{
    private static readonly ExchangeSymbol Btc = new("BTCUSDT");
    private static readonly ExchangeSymbol Eth = new("ETHUSDT");
    private static readonly WatchedSymbol BtcWatched = new(ExchangeEnum.Binance, MarketTypeEnum.Futures, 1, Btc);
    private static readonly WatchedSymbol EthWatched = new(ExchangeEnum.Binance, MarketTypeEnum.Futures, 3, Eth);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Start));
    private readonly FakeLiveTradeStream _stream = new();
    private readonly FakeShockAlertStore _store = new();
    private readonly FakeCandleRepository _candles = new();
    private readonly RecordingNotifier _telegram = new("telegram");
    private readonly FakeWatchedSymbolSource _source = new();
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

    // A symbol normally has the last hour of 1 minute candles stored (the collector keeps them), so the watch starts from them.
    public ShockWatcherListTests() => _candles.Stored.AddRange(MinuteCandles());

    private Task Run() => Task.Run(() =>
        new ShockWatcher(_stream, _store, _candles, [_telegram], Options, _time, NullLogger<ShockWatcher>.Instance).RunAsync(_source, _stop.Token), Ct);

    private async Task StopAsync(Task run)
    {
        _stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Timed out waiting for: {what}");
            await Task.Delay(10, Ct);
        }
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

    /// <summary>Quiet trades with a jump of 1 % at <paramref name="jumpAt"/>, from second <paramref name="from"/> (the fake clock is already there).</summary>
    private static void Feed(ILiveTradeListener listener, ExchangeSymbol symbol, int from, int to, int jumpAt)
    {
        for (var second = from; second < to; second++)
            listener.OnTrade(symbol, Trade(second, Price(second, at: jumpAt, jump: 0.01m)));
    }

    private async Task<ILiveTradeListener> ListenerOfAsync(string symbol)
    {
        await WaitForAsync(() => _stream.ListenerOf(symbol) is not null, $"the subscription of {symbol}");
        return _stream.ListenerOf(symbol)!;
    }

    [Fact]
    public async Task AnEmptyListIsValidAndTheWatchWaitsForASymbol()
    {
        var run = Run();

        await PassAsync(65);

        Assert.Equal(0, _stream.SubscribeCalls);
        Assert.Empty(_telegram.Sent);
        await StopAsync(run);
    }

    [Fact]
    public async Task ASymbolAddedLaterIsFollowedFromTheNextLookAtTheList()
    {
        _source.Symbols.Add(BtcWatched);
        var run = Run();
        await ListenerOfAsync("BTCUSDT");
        Assert.Equal(1, _stream.SubscribeCalls);

        _source.Symbols.Add(EthWatched);
        await PassAsync(30);
        var eth = await ListenerOfAsync("ETHUSDT");

        Feed(eth, Eth, 30, 260, jumpAt: 200);
        await WaitForAsync(() => _telegram.Sent.Count > 0, "the alert");
        Assert.StartsWith("ALARM: ETHUSDT rose", Assert.Single(_telegram.Sent).Title);
        Assert.Equal(3, Assert.Single(_store.Saved).SymbolListingId);
        Assert.Equal(2, _stream.SubscribeCalls);                  // bitcoin was not subscribed again
        await StopAsync(run);
    }

    [Fact]
    public async Task OnlyTheSymbolWithoutMinuteCandlesIsToldToBeWarmingUp()
    {
        // Bitcoin has its candles; the table of ethereum does not exist, so the repository throws for it.
        _candles.Stored.Clear();
        _candles.PerTable["binance_futures_btcusdt_1m"] = MinuteCandles();
        _source.Symbols.AddRange([BtcWatched, EthWatched]);
        var run = Run();

        await ListenerOfAsync("BTCUSDT");
        await ListenerOfAsync("ETHUSDT");
        await WaitForAsync(() => _telegram.Sent.Count > 0, "the warm-up message");
        await PassAsync(3);

        Assert.Equal("Watch is warming up: ETHUSDT", Assert.Single(_telegram.Sent).Title);
        await StopAsync(run);
    }

    [Fact]
    public async Task ASymbolTakenOffTheListIsUnsubscribedAndTheOthersGoOn()
    {
        _source.Symbols.AddRange([BtcWatched, EthWatched]);
        var run = Run();
        var btc = await ListenerOfAsync("BTCUSDT");
        await ListenerOfAsync("ETHUSDT");

        _source.Symbols.Remove(EthWatched);
        await PassAsync(30);
        await WaitForAsync(() => _stream.WasUnsubscribed("ETHUSDT"), "ethereum being unsubscribed");

        Assert.False(_stream.WasUnsubscribed("BTCUSDT"));
        Feed(btc, Btc, 30, 260, jumpAt: 200);
        await WaitForAsync(() => _telegram.Sent.Count > 0, "the alert");
        Assert.StartsWith("ALARM: BTCUSDT rose", Assert.Single(_telegram.Sent).Title);
        await StopAsync(run);
    }

    [Fact]
    public async Task ALateTradeOfASymbolThatWasTakenOffTheListIsIgnored()
    {
        _source.Symbols.AddRange([BtcWatched, EthWatched]);
        var run = Run();
        await ListenerOfAsync("BTCUSDT");
        var eth = await ListenerOfAsync("ETHUSDT");

        _source.Symbols.Remove(EthWatched);
        await PassAsync(30);
        await WaitForAsync(() => _stream.WasUnsubscribed("ETHUSDT"), "ethereum being unsubscribed");

        Feed(eth, Eth, 30, 260, jumpAt: 200);
        await PassAsync(2);

        Assert.Empty(_telegram.Sent);
        await StopAsync(run);
    }

    [Fact]
    public async Task AListThatCannotBeReadKeepsTheSymbolsItHas()
    {
        _source.Symbols.Add(BtcWatched);
        var run = Run();
        var btc = await ListenerOfAsync("BTCUSDT");

        _source.Throws = true;
        await PassAsync(65);

        Assert.False(_stream.WasUnsubscribed("BTCUSDT"));
        Feed(btc, Btc, 65, 300, jumpAt: 250);
        await WaitForAsync(() => _telegram.Sent.Count > 0, "the alert");
        await StopAsync(run);
    }

    [Fact]
    public async Task TheListMayHoldTheSameSymbolTwiceAndItIsFollowedOnce()
    {
        _source.Symbols.AddRange([BtcWatched, BtcWatched with { SymbolListingId = 99 }]);
        var run = Run();

        await ListenerOfAsync("BTCUSDT");
        await PassAsync(65);

        Assert.Equal(1, _stream.SubscribeCalls);
        await StopAsync(run);
    }

    [Fact]
    public async Task SymbolsOfTwoMarketsAreFollowedSideBySide()
    {
        var spotBtc = new WatchedSymbol(ExchangeEnum.Binance, MarketTypeEnum.Spot, 8, new ExchangeSymbol("BTCUSDC"));
        _source.Symbols.AddRange([BtcWatched, spotBtc]);
        var run = Run();

        await ListenerOfAsync("BTCUSDT");
        await ListenerOfAsync("BTCUSDC");
        Assert.Equal(2, _stream.SubscribeCalls);
        await StopAsync(run);
    }

    [Fact]
    public async Task ASymbolWhoseSubscriptionFailedIsTriedAgainAtTheNextLook()
    {
        _stream.Answers.Enqueue(ExchangeResult<IAsyncDisposable>.Fail("the exchange said no"));
        _source.Symbols.Add(BtcWatched);
        var run = Run();
        await WaitForAsync(() => _stream.SubscribeCalls == 1, "the first attempt");

        await PassAsync(30);

        var btc = await ListenerOfAsync("BTCUSDT");
        Assert.Equal(2, _stream.SubscribeCalls);
        Feed(btc, Btc, 30, 260, jumpAt: 200);
        await WaitForAsync(() => _telegram.Sent.Any(m => m.Title.StartsWith("ALARM")), "the alert");
        await StopAsync(run);
    }
}
