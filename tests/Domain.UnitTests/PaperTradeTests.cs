using Domain.Entities;
using Domain.Enums;

namespace Domain.UnitTests;

public class PaperTradeTests
{
    private static DateTime At(int minutes) => PaperTestData.Now.AddMinutes(minutes);

    [Fact]
    public void Open_KeepsWhatTheSignalSaidAndTheEntry()
    {
        var signal = PaperTestData.Long();

        var trade = PaperTrade.Open(signal, 100.2m, PaperTestData.Now, openTradesAtEntry: 2);

        Assert.Equal(signal.SignalId, trade.SignalId);
        Assert.Equal("S1", trade.Strategy);
        Assert.Equal("BTC/USDT", trade.Symbol);
        Assert.Equal(PositionSideEnum.Long, trade.Side);
        Assert.Equal(5m, trade.Leverage);
        Assert.Equal(104m, trade.TakeProfit);
        Assert.Equal(98m, trade.StopLoss);
        Assert.Equal(100m, trade.SignalPrice);
        Assert.Equal(100.2m, trade.EntryPrice);
        Assert.Equal(PaperTestData.Now, trade.OpenedAt);
        Assert.Equal(2, trade.OpenTradesAtEntry);
        Assert.Equal(PaperTradeStatusEnum.Open, trade.Status);
        Assert.True(trade.IsOpen);
        Assert.Null(trade.ExitPrice);
        Assert.Null(trade.ClosedAt);
    }

    [Fact]
    public void Open_WithoutADurationHasNoExpiry() =>
        Assert.Null(PaperTestData.Opened(PaperTestData.Long()).ExpiresAt);

    [Fact]
    public void Open_WithADurationExpiresThatLongAfterTheEntry() =>
        Assert.Equal(PaperTestData.Now.AddHours(48), PaperTestData.Opened(PaperTestData.Long(maxDuration: TimeSpan.FromHours(48))).ExpiresAt);

    [Fact]
    public void RiskReward_IsTheDistanceToTheTargetOverTheDistanceToTheStop() =>
        Assert.Equal(2m, PaperTestData.Opened(PaperTestData.Long()).RiskReward);

    [Fact]
    public void RiskReward_IsMeasuredFromTheEntryNotFromTheSignalPrice() =>
        // Entry 101: 3 to the target, 3 to the stop.
        Assert.Equal(1m, PaperTestData.Opened(PaperTestData.Long(), entry: 101m).RiskReward);

    [Theory]
    [InlineData(98)]
    [InlineData(97)]
    [InlineData(104)]
    [InlineData(105)]
    public void Open_RefusesALongEntryAtOrPastALevel(int entry) =>
        Assert.Throws<ArgumentException>(() => PaperTestData.Opened(PaperTestData.Long(), entry));

    [Theory]
    [InlineData(102)]
    [InlineData(103)]
    [InlineData(96)]
    [InlineData(95)]
    public void Open_RefusesAShortEntryAtOrPastALevel(int entry) =>
        Assert.Throws<ArgumentException>(() => PaperTestData.Opened(PaperTestData.Short(), entry));

    [Fact]
    public void Open_RefusesAnEntryThatIsNotPositive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PaperTestData.Opened(PaperTestData.Long(), 0m));

    // ---- ticks -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Observe_APriceBetweenTheLevelsLeavesTheTradeOpen()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Assert.False(trade.Observe(103.99m, At(1)));
        Assert.False(trade.Observe(98.01m, At(2)));

        Assert.True(trade.IsOpen);
    }

    [Fact]
    public void Observe_ALongClosesOnTheTargetAtThePriceThatReachedIt()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Assert.True(trade.Observe(104m, At(5)));

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.Equal(104m, trade.ExitPrice);
        Assert.Equal(At(5), trade.ClosedAt);
        Assert.False(trade.IsOpen);
    }

    [Fact]
    public void Observe_ALongThatJumpsPastTheTargetExitsAtTheJumpsPrice()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        trade.Observe(105.5m, At(5));

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.Equal(105.5m, trade.ExitPrice);
    }

    [Fact]
    public void Observe_ALongClosesOnTheStop()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        trade.Observe(98m, At(5));

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
        Assert.Equal(98m, trade.ExitPrice);
    }

    [Fact]
    public void Observe_ALongThatJumpsPastTheStopExitsAtTheJumpsPrice()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        trade.Observe(96.4m, At(5));

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
        Assert.Equal(96.4m, trade.ExitPrice);
    }

    [Fact]
    public void Observe_AShortClosesOnTheTarget()
    {
        var trade = PaperTestData.Opened(PaperTestData.Short());

        trade.Observe(96m, At(5));

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.Equal(96m, trade.ExitPrice);
    }

    [Fact]
    public void Observe_AShortClosesOnTheStopAndAJumpPastItKeepsTheJumpsPrice()
    {
        var trade = PaperTestData.Opened(PaperTestData.Short());

        trade.Observe(103m, At(5));

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
        Assert.Equal(103m, trade.ExitPrice);
    }

    [Fact]
    public void Observe_ATradeClosesOnlyOnce()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());
        trade.Observe(104m, At(5));

        Assert.False(trade.Observe(90m, At(6)));
        Assert.False(trade.ObserveCandle(At(6), 90m, 95m, 85m, 90m, At(7)));

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.Equal(104m, trade.ExitPrice);
        Assert.Equal(At(5), trade.ClosedAt);
    }

    [Fact]
    public void Observe_APriceFromBeforeTheEntryIsIgnored()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Assert.False(trade.Observe(90m, PaperTestData.Now.AddSeconds(-1)));

        Assert.True(trade.IsOpen);
    }

    [Fact]
    public void Observe_APriceAtTheMomentOfTheEntryCounts()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Assert.True(trade.Observe(90m, PaperTestData.Now));
    }

    [Fact]
    public void Observe_ClosesAnExpiredTradeAtTheCurrentPrice()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long(maxDuration: TimeSpan.FromHours(24)));

        Assert.False(trade.Observe(101m, PaperTestData.Now.AddHours(24).AddSeconds(-1)));
        Assert.True(trade.Observe(101m, PaperTestData.Now.AddHours(24)));

        Assert.Equal(PaperTradeStatusEnum.Expired, trade.Status);
        Assert.Equal(101m, trade.ExitPrice);
    }

    [Fact]
    public void Observe_AStopThatIsReachedWhenTheTimeIsUpIsAStopNotAnExpiry()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long(maxDuration: TimeSpan.FromHours(24)));

        trade.Observe(97m, PaperTestData.Now.AddHours(25));

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
    }

    [Fact]
    public void Observe_ATradeWithoutADurationNeverExpires()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Assert.False(trade.Observe(101m, PaperTestData.Now.AddDays(400)));
    }

    // ---- candles (catching up after the collector was not watching) --------------------------------------------------------

    private static bool Candle(PaperTrade trade, int openMinutes, decimal open, decimal high, decimal low, decimal close) =>
        trade.ObserveCandle(At(openMinutes), open, high, low, close, At(openMinutes + 1));

    [Fact]
    public void ObserveCandle_ACandleInsideTheLevelsLeavesTheTradeOpen()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Assert.False(Candle(trade, 1, 100m, 103m, 99m, 101m));

        Assert.True(trade.IsOpen);
    }

    [Fact]
    public void ObserveCandle_ALongThatOnlyTouchesTheTargetClosesAtTheTarget()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Candle(trade, 1, 101m, 104.5m, 100m, 104m);

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.Equal(104m, trade.ExitPrice);
        Assert.Equal(At(2), trade.ClosedAt);
    }

    [Fact]
    public void ObserveCandle_ALongThatOnlyTouchesTheStopClosesAtTheStop()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Candle(trade, 1, 99m, 100m, 97.5m, 98.5m);

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
        Assert.Equal(98m, trade.ExitPrice);
    }

    [Fact]
    public void ObserveCandle_WhenBothLevelsAreInsideTheCandleTheStopWins()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Candle(trade, 1, 100m, 105m, 97m, 101m);

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
    }

    [Fact]
    public void ObserveCandle_WhenBothLevelsAreInsideTheCandleTheStopWinsForAShortToo()
    {
        var trade = PaperTestData.Opened(PaperTestData.Short());

        Candle(trade, 1, 100m, 103m, 95m, 100m);

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
    }

    [Fact]
    public void ObserveCandle_ALongThatOpensBelowTheStopExitsAtTheOpen()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Candle(trade, 1, 96.5m, 97m, 95m, 96m);

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
        Assert.Equal(96.5m, trade.ExitPrice);
    }

    [Fact]
    public void ObserveCandle_ALongThatOpensAboveTheTargetExitsAtTheOpen()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Candle(trade, 1, 106m, 107m, 105m, 106m);

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.Equal(106m, trade.ExitPrice);
    }

    [Fact]
    public void ObserveCandle_AShortThatOpensAboveTheStopExitsAtTheOpen()
    {
        var trade = PaperTestData.Opened(PaperTestData.Short());

        Candle(trade, 1, 103.5m, 104m, 103m, 103m);

        Assert.Equal(PaperTradeStatusEnum.StopLoss, trade.Status);
        Assert.Equal(103.5m, trade.ExitPrice);
    }

    [Fact]
    public void ObserveCandle_AShortThatOpensBelowTheTargetExitsAtTheOpen()
    {
        var trade = PaperTestData.Opened(PaperTestData.Short());

        Candle(trade, 1, 95m, 96m, 94m, 95m);

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.Equal(95m, trade.ExitPrice);
    }

    [Fact]
    public void ObserveCandle_ACandleThatOpenedBeforeTheEntryIsIgnored()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Assert.False(Candle(trade, -1, 100m, 110m, 90m, 100m));

        Assert.True(trade.IsOpen);
    }

    [Fact]
    public void ObserveCandle_ACandleThatOpensAtTheMomentOfTheEntryCounts()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Assert.True(Candle(trade, 0, 100m, 110m, 100m, 109m));
    }

    [Fact]
    public void ObserveCandle_ExpiresAtTheCandlesCloseWhenTheTimeIsUp()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long(maxDuration: TimeSpan.FromMinutes(10)));

        Assert.False(Candle(trade, 8, 100m, 101m, 99m, 100.5m));
        Assert.True(Candle(trade, 9, 100m, 101m, 99m, 100.7m));

        Assert.Equal(PaperTradeStatusEnum.Expired, trade.Status);
        Assert.Equal(100.7m, trade.ExitPrice);
        Assert.Equal(At(10), trade.ClosedAt);
    }
}
