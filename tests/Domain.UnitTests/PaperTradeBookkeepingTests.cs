using Domain.Entities;
using Domain.Enums;

namespace Domain.UnitTests;

/// <summary>What a paper trade keeps besides its state: the best and worst price, its message and its result.</summary>
public class PaperTradeBookkeepingTests
{
    private static DateTime At(int minutes) => PaperTestData.Now.AddMinutes(minutes);

    [Fact]
    public void BestAndWorstStartAtTheEntryPrice()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long(), 100.2m);

        Assert.Equal(100.2m, trade.BestPrice);
        Assert.Equal(100.2m, trade.WorstPrice);
    }

    [Fact]
    public void ALongKeepsItsHighestAndLowestPrice()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        trade.Observe(102m, At(1));
        trade.Observe(99m, At(2));
        trade.Observe(101m, At(3));

        Assert.Equal(102m, trade.BestPrice);
        Assert.Equal(99m, trade.WorstPrice);
    }

    [Fact]
    public void AShortKeepsItsLowestPriceAsTheBestAndItsHighestAsTheWorst()
    {
        var trade = PaperTestData.Opened(PaperTestData.Short());

        trade.Observe(98m, At(1));
        trade.Observe(101m, At(2));

        Assert.Equal(98m, trade.BestPrice);
        Assert.Equal(101m, trade.WorstPrice);
    }

    [Fact]
    public void ThePriceThatEndsTheTradeCountsToo()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        trade.Observe(98m, At(1));

        Assert.Equal(98m, trade.WorstPrice);
    }

    [Fact]
    public void APriceFromBeforeTheEntryAndAPriceAfterTheEndChangeNothing()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());
        trade.Observe(90m, PaperTestData.Now.AddSeconds(-1));
        trade.Observe(104m, At(1));

        trade.Observe(120m, At(2));

        Assert.Equal(104m, trade.BestPrice);
        Assert.Equal(100m, trade.WorstPrice);
    }

    [Fact]
    public void ACandleThatLeavesTheTradeOpenAddsItsHighAndItsLow()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        trade.ObserveCandle(At(1), 100m, 103m, 99m, 101m, At(2));

        Assert.True(trade.IsOpen);
        Assert.Equal(103m, trade.BestPrice);
        Assert.Equal(99m, trade.WorstPrice);
    }

    [Fact]
    public void ACandleThatEndsTheTradeCountsOnlyTheExitNotWhatCameAfterIt()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        trade.ObserveCandle(At(1), 99m, 100m, 95m, 96m, At(2));   // stops at 98, the low of 95 came later

        Assert.Equal(98m, trade.WorstPrice);
    }

    [Fact]
    public void AMessageCanBeAttachedOnce()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        trade.AttachMessage(-1001234567890, 42);

        Assert.Equal(-1001234567890, trade.TelegramChatId);
        Assert.Equal(42, trade.TelegramMessageId);
        Assert.Throws<InvalidOperationException>(() => trade.AttachMessage(-1001234567890, 43));
        Assert.Equal(42, trade.TelegramMessageId);
    }

    [Fact]
    public void SettleKeepsTheResultOnTheTrade()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long(stop: 98, target: 102));
        trade.Observe(102m, PaperTestData.Now.AddDays(11));

        var pnl = trade.Settle([new FundingRate(PaperTestData.Now.AddHours(5), 0.0001m, 101m)]);

        Assert.Equal(0.10m, trade.GrossResult);
        Assert.Equal(0.00404m, trade.FeesPaid);
        Assert.Equal(0.000505m, trade.FundingPaid);
        Assert.Equal(pnl.Net, trade.NetResult);
        Assert.Equal(1m, trade.RMultiple);
    }

    [Fact]
    public void ATradeThatIsStillOpenCannotBeSettled()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());

        Assert.Throws<InvalidOperationException>(() => trade.Settle([]));
        Assert.Null(trade.NetResult);
    }

    [Fact]
    public void ATradeHasNoResultUntilItIsSettled()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());
        trade.Observe(104m, At(1));

        Assert.Equal(PaperTradeStatusEnum.TakeProfit, trade.Status);
        Assert.Null(trade.GrossResult);
        Assert.Null(trade.NetResult);
    }
}
