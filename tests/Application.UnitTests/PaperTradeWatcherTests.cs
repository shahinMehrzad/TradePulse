using Application.Exchange;
using Application.Signals;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Application.UnitTests.SignalTestData;

namespace Application.UnitTests;

/// <summary>The watch of the open paper trades: ticks, the end of a trade, restarts, lost connections.</summary>
public class PaperTradeWatcherTests
{
    private static readonly ExchangeSymbol BtcName = new("BTCUSDT");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Opened.AddMinutes(30).AddSeconds(10)));   // 08:30:12
    private readonly FakeLiveTradeStream _stream = new();
    private readonly FakeSignalStore _store = new();
    private readonly FakeCandleGateway _candles = new();
    private readonly FakeHistoricalTrades _history = new();
    private readonly FakeFollowedSymbols _symbols = new(Btc);
    private readonly RecordingTradeEvents _events = new();
    private readonly PriceBoard _board = new();
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

    private WatcherOptions _options = new();

    private PaperTradeWatcher NewWatcher() => new(
        _stream, _store, _symbols, new TradeCatchUp(_candles, _history, _time, NullLogger<TradeCatchUp>.Instance),
        _events, _board, _options, _time, NullLogger<PaperTradeWatcher>.Instance);

    private PaperTradeWatcher? _watcher;

    private Task Run()
    {
        _watcher = NewWatcher();
        return Task.Run(() => _watcher.RunAsync(_stop.Token), Ct);
    }

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

    private async Task<ILiveTradeListener> SubscribedAsync(string symbol = "BTCUSDT")
    {
        await WaitForAsync(() => _stream.ListenerOf(symbol) is not null, $"the subscription of {symbol}");
        return _stream.ListenerOf(symbol)!;
    }

    private async Task PassAsync(int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(15, Ct);
        }
    }

    /// <summary>A trade of BTC a few seconds after the clock's now (08:30:12), so it is after the entry.</summary>
    private LiveTrade BtcTrade(decimal price, int secondsAfterNow = 1) => new(_time.GetUtcNow().UtcDateTime.AddSeconds(secondsAfterNow), price, 1m);

    [Fact]
    public async Task AnOpenTradeEndsWhenThePriceReachesItsTarget()
    {
        var trade = Long();
        _store.Open.Add(trade);
        var run = Run();
        var listener = await SubscribedAsync();

        listener.OnTrade(BtcName, BtcTrade(104.5m));
        await WaitForAsync(() => _events.Closed.Count == 1, "the end of the trade");

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.Equal(104.5m, trade.ExitPrice);
        Assert.Same(trade, Assert.Single(_store.Closed));
        await StopAsync(run);
    }

    [Fact]
    public async Task APriceBetweenTheLevelsChangesNothingAndIsOnTheBoard()
    {
        var trade = Long();
        _store.Open.Add(trade);
        var run = Run();
        var listener = await SubscribedAsync();

        listener.OnTrade(BtcName, BtcTrade(101.5m));
        await WaitForAsync(() => _board.Get(ExchangeEnum.Binance, "BTC/USDT", _time.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(1)) is not null, "the price on the board");

        Assert.True(trade.IsOpen);
        Assert.Equal(101.5m, _board.Get(ExchangeEnum.Binance, "BTC/USDT", _time.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(1))!.Price);
        Assert.Equal(101.5m, trade.BestPrice);
        await StopAsync(run);
    }

    [Fact]
    public async Task AShortEndsOnItsStop()
    {
        var trade = Short();
        _store.Open.Add(trade);
        var run = Run();
        var listener = await SubscribedAsync();

        listener.OnTrade(BtcName, BtcTrade(102.3m));
        await WaitForAsync(() => _events.Closed.Count == 1, "the end of the trade");

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
        await StopAsync(run);
    }

    [Fact]
    public async Task ManyPricesPastTheTargetEndTheTradeOnceAndTellOnce()
    {
        _store.Open.Add(Long());
        var run = Run();
        var listener = await SubscribedAsync();

        foreach (var price in new[] { 104.2m, 105m, 90m, 104.4m })
            listener.OnTrade(BtcName, BtcTrade(price));
        await WaitForAsync(() => _events.Closed.Count >= 1, "the end of the trade");
        await PassAsync(2);

        Assert.Single(_store.Closed);
        Assert.Single(_events.Closed);
        Assert.Equal(104.2m, _store.Closed[0].ExitPrice);
        await StopAsync(run);
    }

    [Fact]
    public async Task ATradeHandedOverAfterTheStartIsFollowed()
    {
        var run = Run();
        var listener = await SubscribedAsync();
        var trade = Long();

        _watcher!.Track(trade);
        await PassAsync(1);
        listener.OnTrade(BtcName, BtcTrade(98m));
        await WaitForAsync(() => _events.Closed.Count == 1, "the end of the handed over trade");

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
        await StopAsync(run);
    }

    [Fact]
    public async Task ATradeReadAndHandedOverTogetherIsFollowedOnce()
    {
        var trade = Long();
        _store.Open.Add(trade);
        var run = Run();
        var listener = await SubscribedAsync();

        _watcher!.Track(trade);
        await PassAsync(1);
        listener.OnTrade(BtcName, BtcTrade(104.5m));
        await WaitForAsync(() => _events.Closed.Count == 1, "the end of the trade");
        await PassAsync(1);

        Assert.Single(_events.Closed);
        await StopAsync(run);
    }

    [Fact]
    public async Task ASymbolWithAnOpenTradeIsFollowedEvenWhenItIsNotListed()
    {
        _symbols.Symbols.Clear();
        _store.Open.Add(Long());
        var run = Run();

        var listener = await SubscribedAsync();   // BTCUSDT, taken from the trade's own name

        Assert.NotNull(listener);
        await StopAsync(run);
    }

    [Fact]
    public async Task ASymbolListedLaterIsFollowedWithinTheRefresh()
    {
        _options = new WatcherOptions { SymbolRefresh = TimeSpan.FromSeconds(5) };
        var run = Run();
        await SubscribedAsync();

        _symbols.Symbols.Add(Eth);
        await PassAsync(6);

        Assert.NotNull(await SubscribedAsync("ETHUSDT"));
        await StopAsync(run);
    }

    [Fact]
    public async Task ASymbolThatIsNotListedAnyMoreAndHasNoTradeIsLetGo()
    {
        _options = new WatcherOptions { SymbolRefresh = TimeSpan.FromSeconds(5) };
        var run = Run();
        await SubscribedAsync();

        _symbols.Symbols.Clear();
        await PassAsync(6);

        await WaitForAsync(() => _stream.WasUnsubscribed("BTCUSDT"), "the unsubscription");
        await StopAsync(run);
    }

    [Fact]
    public async Task TheTradeOfAnotherSymbolHandedOverGetsItsOwnSubscription()
    {
        var run = Run();
        await SubscribedAsync();

        _watcher!.Track(Long(symbol: "ETH/USDT", id: 5));
        await PassAsync(2);

        Assert.NotNull(await SubscribedAsync("ETHUSDT"));
        await StopAsync(run);
    }

    [Fact]
    public async Task ASubscriptionTheExchangeRefusedIsTriedAgainSoon()
    {
        _options = new WatcherOptions { SubscribeRetry = TimeSpan.FromSeconds(10) };
        _stream.Answers.Enqueue(ExchangeResult<IAsyncDisposable>.Fail("rate limited"));
        var run = Run();
        await WaitForAsync(() => _stream.SubscribeCalls == 1, "the first try");

        await PassAsync(11);

        Assert.NotNull(await SubscribedAsync());
        await StopAsync(run);
    }

    // ---- the database ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEndThatCouldNotBeStoredIsStoredAgainAndToldOnce()
    {
        _store.CloseFailures = 1;
        _store.Open.Add(Long());
        var run = Run();
        var listener = await SubscribedAsync();

        listener.OnTrade(BtcName, BtcTrade(104.5m));
        await WaitForAsync(() => _store.CloseFailures == 0, "the failed attempt");
        Assert.Empty(_events.Closed);

        await PassAsync(2);
        await WaitForAsync(() => _events.Closed.Count == 1, "the second attempt");

        Assert.Single(_store.Closed);
        await StopAsync(run);
    }

    [Fact]
    public async Task AnEndThatSomeoneElseStoredFirstIsNotToldAgain()
    {
        _store.EndedElsewhere = true;
        var trade = Long();
        _store.Open.Add(trade);
        var run = Run();
        var listener = await SubscribedAsync();

        listener.OnTrade(BtcName, BtcTrade(104.5m));
        await WaitForAsync(() => trade.Status == PaperTradeStatusEnum.TakeProfit, "the trade to end in memory");
        await PassAsync(2);

        Assert.Empty(_events.Closed);
        Assert.Empty(_store.Closed);
        await StopAsync(run);
    }

    [Fact]
    public async Task AFailureTellingAboutTheEndDoesNotStopTheWatch()
    {
        _events.Throws = true;
        _store.Open.Add(Long(id: 1));
        _store.Open.Add(Long(id: 2));
        var run = Run();
        var listener = await SubscribedAsync();

        listener.OnTrade(BtcName, BtcTrade(104.5m));
        await WaitForAsync(() => _events.Closed.Count == 2, "both ends told, one failing after the other");

        Assert.Equal(2, _store.Closed.Count);
        await StopAsync(run);
    }

    [Fact]
    public async Task TheOpenTradesAreReadAgainWhenTheDatabaseDidNotAnswerAtTheStart()
    {
        _options = new WatcherOptions { StartRetry = TimeSpan.FromSeconds(5) };
        var failing = new ThrowingOnceStore(_store);
        var watcher = new PaperTradeWatcher(
            _stream, failing, _symbols, new TradeCatchUp(_candles, _history, _time, NullLogger<TradeCatchUp>.Instance),
            _events, _board, _options, _time, NullLogger<PaperTradeWatcher>.Instance);
        _store.Open.Add(Long());
        var run = Task.Run(() => watcher.RunAsync(_stop.Token), Ct);
        await WaitForAsync(() => failing.Reads == 1, "the first, failing read");

        await PassAsync(6);

        var listener = await SubscribedAsync();
        listener.OnTrade(BtcName, BtcTrade(104.5m));
        await WaitForAsync(() => _events.Closed.Count == 1, "the end of the trade that was read the second time");
        await StopAsync(run);
    }

    // ---- what nobody watched --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ATradeThatReachedItsTargetWhileTheServiceWasStoppedEndsAtTheStart()
    {
        var trade = Long();
        _store.Open.Add(trade);
        _candles.Add(Quiet(1, 9).ToArray());
        _candles.Add(Minute(10, 100m, 105m, 100m, 104m));
        _candles.Add(Quiet(11, 29).ToArray());
        var run = Run();

        await WaitForAsync(() => _events.Closed.Count == 1, "the end found in the candles");

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.Equal(104m, trade.ExitPrice);
        await StopAsync(run);
    }

    [Fact]
    public async Task AConnectionThatComesBackIsFollowedByALookAtWhatItMissed()
    {
        var trade = Long();
        _store.Open.Add(trade);
        _candles.Add(Quiet(1, 29).ToArray());
        var run = Run();
        var listener = await SubscribedAsync();
        await PassAsync(1);
        Assert.True(trade.IsOpen);

        // Ten minutes pass; the stop was reached in the 08:35 minute while the connection was down.
        _candles.Add(Minute(35, 100m, 100.5m, 97.5m, 98.5m), Minute(36, 99m, 100m, 98.5m, 99m));
        _time.Advance(TimeSpan.FromMinutes(10));
        listener.OnConnectionRestored(TimeSpan.FromMinutes(9));
        await PassAsync(2);
        await WaitForAsync(() => _events.Closed.Count == 1, "the end found after the reconnection");

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
        await StopAsync(run);
    }

    [Fact]
    public async Task ALookAtThePastThatCouldNotBeFinishedIsTriedAgain()
    {
        _options = new WatcherOptions { CatchUpRetry = TimeSpan.FromSeconds(30) };
        var trade = Long();
        _store.Open.Add(trade);
        _candles.Fail = true;
        _candles.Add(Quiet(1, 9).ToArray());
        _candles.Add(Minute(10, 100m, 105m, 100m, 104m));
        _candles.Add(Quiet(11, 29).ToArray());
        var run = Run();
        await SubscribedAsync();
        await WaitForAsync(() => _candles.Calls.Count >= 1, "the first, failing look");
        Assert.True(trade.IsOpen);

        _candles.Fail = false;
        await PassAsync(31);
        await WaitForAsync(() => _events.Closed.Count == 1, "the end found by the second look");

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        await StopAsync(run);
    }

    [Fact]
    public async Task TheFirstHalfOfTheMinuteTheSubscriptionStartedInIsLookedAtOnceItHasClosed()
    {
        // Opened at 08:20:10, the service starts at 08:30:12: the minute 08:30 is half over when the subscription begins.
        var trade = Long(openedAt: FirstMinute.AddMinutes(20).AddSeconds(10));
        _store.Open.Add(trade);
        _candles.Add(Quiet(21, 29).ToArray());
        _candles.Add(Minute(30, 100m, 104.5m, 100m, 104m));   // the target was reached in the first half of that minute
        var run = Run();
        await SubscribedAsync();
        await PassAsync(1);
        Assert.True(trade.IsOpen);

        await PassAsync(60);   // 08:31:13: the candle of 08:30 has closed and its delay has passed

        await WaitForAsync(() => _events.Closed.Count == 1, "the end found in the candle of the first half-minute");
        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        await StopAsync(run);
    }

    // ---- the rest ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheProgressOfTheOpenTradesIsSavedEveryMinute()
    {
        _store.Open.Add(Long());
        var run = Run();
        var listener = await SubscribedAsync();
        listener.OnTrade(BtcName, BtcTrade(101m));
        await WaitForAsync(() => _board.Get(ExchangeEnum.Binance, "BTC/USDT", _time.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(1)) is not null, "the price");
        var before = _store.SaveCalls;

        await PassAsync(61);

        Assert.True(_store.SaveCalls > before);
        await StopAsync(run);
    }

    [Fact]
    public async Task StoppingTheWatchUnsubscribes()
    {
        var run = Run();
        await SubscribedAsync();

        await StopAsync(run);

        Assert.True(_stream.WasUnsubscribed("BTCUSDT"));
    }

    [Fact]
    public async Task AListThatCannotBeReadKeepsTheSymbolsAlreadyFollowed()
    {
        _options = new WatcherOptions { SymbolRefresh = TimeSpan.FromSeconds(5) };
        var run = Run();
        var listener = await SubscribedAsync();

        _symbols.Throws = true;
        await PassAsync(6);

        Assert.False(_stream.WasUnsubscribed("BTCUSDT"));
        listener.OnTrade(BtcName, BtcTrade(101m));
        await WaitForAsync(() => _board.Get(ExchangeEnum.Binance, "BTC/USDT", _time.GetUtcNow().UtcDateTime, TimeSpan.FromMinutes(1)) is not null, "the price still arriving");
        await StopAsync(run);
    }

    /// <summary>A store whose first read of the open trades fails, as a database that is not up yet.</summary>
    private sealed class ThrowingOnceStore(FakeSignalStore inner) : Application.Signals.ISignalStore
    {
        private int _reads;

        public int Reads => Volatile.Read(ref _reads);

        public Task<IReadOnlyList<PaperTrade>> GetOpenTradesAsync(CancellationToken cancellationToken) =>
            Interlocked.Increment(ref _reads) == 1 ? throw new InvalidOperationException("the database is not up") : inner.GetOpenTradesAsync(cancellationToken);

        public Task<bool> CloseAsync(PaperTrade trade, CancellationToken cancellationToken) => inner.CloseAsync(trade, cancellationToken);
        public Task SaveProgressAsync(IReadOnlyCollection<PaperTrade> openTrades, CancellationToken cancellationToken) => inner.SaveProgressAsync(openTrades, cancellationToken);
        public Task<bool> SaveResultAsync(PaperTrade trade, CancellationToken cancellationToken) => inner.SaveResultAsync(trade, cancellationToken);
        public Task<IReadOnlyList<PaperTrade>> GetUnsettledAsync(CancellationToken cancellationToken) => inner.GetUnsettledAsync(cancellationToken);
        public Task<bool> RecordAsync(ReceivedSignal signal, CancellationToken cancellationToken) => inner.RecordAsync(signal, cancellationToken);
        public Task<int> CountOpenAsync(ExchangeEnum exchange, string strategy, string symbol, CancellationToken cancellationToken) => inner.CountOpenAsync(exchange, strategy, symbol, cancellationToken);
        public Task AddTradeAsync(PaperTrade trade, CancellationToken cancellationToken) => inner.AddTradeAsync(trade, cancellationToken);
        public Task<bool> AttachMessageAsync(int tradeId, long chatId, int messageId, CancellationToken cancellationToken) => inner.AttachMessageAsync(tradeId, chatId, messageId, cancellationToken);
        public Task AddEventAsync(SignalEvent signalEvent, CancellationToken cancellationToken) => inner.AddEventAsync(signalEvent, cancellationToken);
    }
}
