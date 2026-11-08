using Application.Exchange;
using Application.Signals;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Application.UnitTests.SignalTestData;

namespace Application.UnitTests;

/// <summary>Looking at a stretch of price nobody watched, through the 1 minute candles. Nothing is guessed.</summary>
public class TradeCatchUpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Ten past eight and half a minute: the minutes up to 08:09 are closed.
    private static readonly DateTime Now = FirstMinute.AddMinutes(10).AddSeconds(30);
    private static readonly DateTime LastClosedEnd = FirstMinute.AddMinutes(10);

    private readonly FakeCandleGateway _candles = new();
    private readonly FakeHistoricalTrades _history = new();
    private readonly List<PaperTrade> _ended = [];

    private TradeCatchUp NewCatchUp() => new(_candles, _history, new FakeTimeProvider(new DateTimeOffset(Now)), NullLogger<TradeCatchUp>.Instance);

    private Task<bool> RunAsync(IReadOnlyList<PaperTrade> trades, DateTime? from = null) => NewCatchUp().RunAsync(Btc, trades, from, _ended, Ct);

    private static LiveTrade Tick(int minute, int second, decimal price) => new(FirstMinute.AddMinutes(minute).AddSeconds(second), price, 1m);

    [Fact]
    public async Task QuietMinutesLeaveTheTradeOpenAndMoveItsLastCheckToTheLastClosedMinute()
    {
        var trade = Long();
        _candles.Add(Quiet(1, 9).ToArray());

        Assert.True(await RunAsync([trade]));

        Assert.True(trade.IsOpen);
        Assert.Empty(_ended);
        Assert.Equal(LastClosedEnd, trade.LastCheckedAt);
        Assert.Equal((101m, 99m), (trade.BestPrice, trade.WorstPrice));
    }

    [Fact]
    public async Task ItAsksFromTheMinuteTheTradeWasLastCheckedToTheLastClosedMinute()
    {
        _candles.Add(Quiet(1, 9).ToArray());

        await RunAsync([Long()]);

        var call = Assert.Single(_candles.Calls);
        Assert.Equal(FirstMinute, call.From);
        Assert.Equal(FirstMinute.AddMinutes(9), call.To);
    }

    [Fact]
    public async Task TheMinuteInProgressIsNeverUsedEvenIfTheExchangeSendsIt()
    {
        var trade = Long();
        _candles.IgnoreTo = true;
        _candles.Add(Quiet(1, 9).ToArray());
        _candles.Add(Minute(10, 100m, 106m, 100m, 105m));   // the minute that is still running: it would end the trade

        Assert.True(await RunAsync([trade]));

        Assert.True(trade.IsOpen);
        Assert.Equal(LastClosedEnd, trade.LastCheckedAt);
    }

    [Fact]
    public async Task ACandleThatReachesOnlyTheTargetEndsTheTradeAtTheTarget()
    {
        var trade = Long();
        _candles.Add(Quiet(1, 4).ToArray());
        _candles.Add(Minute(5, 101m, 104.5m, 100m, 104m));
        _candles.Add(Quiet(6, 9).ToArray());

        Assert.True(await RunAsync([trade]));

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.Equal(104m, trade.ExitPrice);
        Assert.Equal(FirstMinute.AddMinutes(6), trade.ClosedAt);
        Assert.Same(trade, Assert.Single(_ended));
    }

    [Fact]
    public async Task ACandleThatOpensBeyondTheStopEndsTheTradeAtItsOpen()
    {
        var trade = Long();
        _candles.Add(Minute(3, 97m, 99m, 96m, 98m));

        await RunAsync([trade]);

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
        Assert.Equal(97m, trade.ExitPrice);
    }

    [Fact]
    public async Task AMinuteWithBothLevelsIsReplayedFromItsTradesTargetFirst()
    {
        var trade = Long();
        _candles.Add(Quiet(1, 2).ToArray());
        _candles.Add(Minute(3, 100m, 105m, 97m, 101m));
        _history.Add(FirstMinute.AddMinutes(3), Tick(3, 10, 100.5m), Tick(3, 20, 104.2m), Tick(3, 40, 97m));

        Assert.True(await RunAsync([trade]));

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.Equal(104.2m, trade.ExitPrice);
        Assert.Equal(FirstMinute.AddMinutes(3).AddSeconds(20), trade.ClosedAt);
    }

    [Fact]
    public async Task AMinuteWithBothLevelsIsReplayedFromItsTradesStopFirst()
    {
        var trade = Long();
        _candles.Add(Minute(3, 100m, 105m, 97m, 101m));
        _history.Add(FirstMinute.AddMinutes(3), Tick(3, 5, 99.5m), Tick(3, 15, 97.5m), Tick(3, 30, 105m));

        await RunAsync([trade]);

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
        Assert.Equal(97.5m, trade.ExitPrice);
    }

    [Fact]
    public async Task TheTradesOfAMinuteAreReplayedInTheOrderTheyHappenedNotTheOrderTheyCame()
    {
        var trade = Long();
        _candles.Add(Minute(3, 100m, 105m, 97m, 101m));
        _history.Add(FirstMinute.AddMinutes(3), Tick(3, 40, 97m), Tick(3, 10, 104.2m));   // the target was first

        await RunAsync([trade]);

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
    }

    [Fact]
    public async Task AMinuteWithBothLevelsThatLeavesTheTradeOpenInTheEndIsStillFollowed()
    {
        var trade = Long();
        _candles.Add(Minute(3, 100m, 105m, 97m, 101m));
        // The candle's extremes (105 and 97) may be other trades' doing in the same minute; the trades replayed for this one never reach a level.
        _history.Add(FirstMinute.AddMinutes(3), Tick(3, 10, 100.5m), Tick(3, 20, 103.9m));

        Assert.True(await RunAsync([trade]));

        Assert.True(trade.IsOpen);
        Assert.Equal(103.9m, trade.BestPrice);
    }

    [Fact]
    public async Task WhenTheTradesOfThatMinuteCannotBeReadTheTradeStaysOpenAndNoLaterCandleIsUsed()
    {
        var trade = Long();
        _candles.Add(Quiet(1, 2).ToArray());
        _candles.Add(Minute(3, 100m, 105m, 97m, 101m));      // both levels
        _candles.Add(Minute(4, 100m, 105m, 100m, 104m));     // would end it at the target, but it comes after the unknown minute
        _history.Fail = true;

        Assert.False(await RunAsync([trade]));

        Assert.True(trade.IsOpen);
        Assert.Empty(_ended);
        Assert.Equal(FirstMinute.AddMinutes(3), trade.LastCheckedAt);   // up to the end of the last minute that was known
    }

    [Fact]
    public async Task OnceTheTradesCanBeReadTheSameStretchGivesTheCertainAnswer()
    {
        var trade = Long();
        _candles.Add(Minute(3, 100m, 105m, 97m, 101m));
        _history.Fail = true;
        Assert.False(await RunAsync([trade]));

        _history.Fail = false;
        _history.Add(FirstMinute.AddMinutes(3), Tick(3, 10, 97.4m), Tick(3, 20, 105m));
        Assert.True(await RunAsync([trade]));

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
        Assert.Equal(97.4m, trade.ExitPrice);
    }

    [Fact]
    public async Task WhenTheCandlesCannotBeReadNothingChanges()
    {
        var trade = Long();
        _candles.Fail = true;

        Assert.False(await RunAsync([trade]));

        Assert.True(trade.IsOpen);
        Assert.Equal(Opened, trade.LastCheckedAt);
    }

    [Fact]
    public async Task ACandleBeforeTheLastCheckIsNotLookedAtAgain()
    {
        var trade = Long();
        trade.MarkChecked(FirstMinute.AddMinutes(7).AddSeconds(5));
        _candles.Add(Minute(6, 100m, 105m, 100m, 104m));   // before the last check: it was seen already
        _candles.Add(Quiet(7, 9).ToArray());

        await RunAsync([trade]);

        Assert.True(trade.IsOpen);
    }

    [Fact]
    public async Task AnEarlierStartGivenByTheCallerOverridesTheLastCheck()
    {
        var trade = Long();
        trade.MarkChecked(FirstMinute.AddMinutes(7));
        _candles.Add(Minute(6, 100m, 105m, 100m, 104m));
        _candles.Add(Quiet(7, 9).ToArray());

        await RunAsync([trade], from: FirstMinute.AddMinutes(5));

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
    }

    [Fact]
    public async Task MinutesInWhichNothingWasTradedDoNotStopIt()
    {
        var trade = Long();
        _candles.Add(Minute(1, 100m, 101m, 99m, 100m), Minute(4, 100m, 101m, 99m, 100m));   // 2, 3 and 5 to 9 have no candle

        Assert.True(await RunAsync([trade]));

        Assert.Equal(LastClosedEnd, trade.LastCheckedAt);
    }

    [Fact]
    public async Task SeveralPagesAreReadUntilTheEnd()
    {
        var trade = Long();
        _candles.PageSize = 3;
        _candles.Add(Quiet(1, 6).ToArray());
        _candles.Add(Minute(7, 100m, 105m, 100m, 104m));

        Assert.True(await RunAsync([trade]));

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.True(_candles.Calls.Count >= 3);
    }

    [Fact]
    public async Task EachTradeOfTheSymbolIsJudgedByItsOwnLevels()
    {
        var @long = Long(id: 1);
        var @short = Short(id: 2);
        _candles.Add(Minute(2, 100m, 104.5m, 100m, 104m));   // reaches the long's target (104) and the short's stop (102), neither's other level

        await RunAsync([@long, @short]);

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, @long.Status);
        Assert.Equal(PaperTradeStatusEnum.StopLoss, @short.Status);
        Assert.Equal(2, _ended.Count);
    }

    [Fact]
    public async Task ATradeThatAlreadyEndedIsLeftAlone()
    {
        var trade = Long();
        trade.Observe(104m, Opened.AddSeconds(5));
        _candles.Add(Quiet(1, 9).ToArray());

        Assert.True(await RunAsync([trade]));

        Assert.Empty(_ended);
        Assert.Empty(_candles.Calls);
    }

    [Fact]
    public async Task AnEmptyListIsDoneAtOnce()
    {
        Assert.True(await RunAsync([]));
        Assert.Empty(_candles.Calls);
    }
}
