using Domain.Entities;
using Domain.ValueObjects;

namespace Domain.UnitTests;

public class PaperPnlTests
{
    private static readonly DateTime Opened = PaperTestData.Now;
    private static readonly DateTime Closed = Opened.AddDays(11);

    private static FundingRate Funding(DateTime at, decimal rate, decimal mark) => new(at, rate, mark);

    /// <summary>A long at 100 with the target at 102 and the stop at 98, leverage 5, closed at the target after 11 days.</summary>
    private static PaperTrade WonLong()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long(stop: 98, target: 102));
        trade.Observe(102m, Closed);
        return trade;
    }

    /// <summary>A short at 100 with the target at 98 and the stop at 102, leverage 5, closed at the target after 11 days.</summary>
    private static PaperTrade WonShort()
    {
        var trade = PaperTestData.Opened(PaperTestData.Short(stop: 102, target: 98));
        trade.Observe(98m, Closed);
        return trade;
    }

    [Fact]
    public void ALongThatWinsEarnsTheMoveTimesTheLeverage()
    {
        var pnl = PaperPnl.Calculate(WonLong(), []);

        Assert.Equal(0.10m, pnl.Gross);
    }

    [Fact]
    public void AShortThatWinsEarnsTheMoveDownTimesTheLeverage()
    {
        var pnl = PaperPnl.Calculate(WonShort(), []);

        Assert.Equal(0.10m, pnl.Gross);
    }

    [Fact]
    public void ALongThatLosesLosesTheMoveTimesTheLeverage()
    {
        var trade = PaperTestData.Opened(PaperTestData.Long());
        trade.Observe(98m, Closed);

        Assert.Equal(-0.10m, PaperPnl.Calculate(trade, []).Gross);
    }

    [Fact]
    public void AShortThatLosesLosesTheMoveUpTimesTheLeverage()
    {
        var trade = PaperTestData.Opened(PaperTestData.Short());
        trade.Observe(102m, Closed);

        Assert.Equal(-0.10m, PaperPnl.Calculate(trade, []).Gross);
    }

    [Fact]
    public void TheFeeIsPaidOnTheValueAtEntryAndOnTheValueAtExit()
    {
        // 0.04 % x 5 x (1 + 1.02): the exit is worth 2 % more than the entry.
        Assert.Equal(0.00404m, PaperPnl.Calculate(WonLong(), []).Fees);
        // The short exits 2 % lower: 0.04 % x 5 x (1 + 0.98).
        Assert.Equal(0.00396m, PaperPnl.Calculate(WonShort(), []).Fees);
    }

    [Fact]
    public void TheFeeRateCanBeChanged() =>
        Assert.Equal(0.00202m, PaperPnl.Calculate(WonLong(), [], takerFee: 0.0002m).Fees);

    [Fact]
    public void ALongPaysAPositiveFundingRateAtTheMarkPrice()
    {
        // 0.01 % x 5 x (101 / 100).
        var pnl = PaperPnl.Calculate(WonLong(), [Funding(Opened.AddHours(5), 0.0001m, 101m)]);

        Assert.Equal(0.000505m, pnl.Funding);
    }

    [Fact]
    public void ALongReceivesANegativeFundingRate()
    {
        var pnl = PaperPnl.Calculate(WonLong(), [Funding(Opened.AddHours(5), -0.0001m, 101m)]);

        Assert.Equal(-0.000505m, pnl.Funding);
    }

    [Fact]
    public void AShortReceivesAPositiveFundingRate()
    {
        var pnl = PaperPnl.Calculate(WonShort(), [Funding(Opened.AddHours(5), 0.0001m, 99m)]);

        Assert.Equal(-0.000495m, pnl.Funding);
    }

    [Fact]
    public void AShortPaysANegativeFundingRate()
    {
        var pnl = PaperPnl.Calculate(WonShort(), [Funding(Opened.AddHours(5), -0.0001m, 99m)]);

        Assert.Equal(0.000495m, pnl.Funding);
    }

    [Fact]
    public void EveryFundingSettlementWhileTheTradeWasOpenIsAdded()
    {
        var history = Enumerable.Range(1, 33)
            .Select(i => Funding(Opened.AddHours(8 * i - 1), 0.0001m, 100m))
            .ToList();

        // 33 settlements: 0.01 % x 5 each.
        Assert.Equal(33 * 0.0005m, PaperPnl.Calculate(WonLong(), history).Funding);
    }

    [Fact]
    public void ASettlementAtTheMomentOfTheEntryIsPaid() =>
        Assert.Equal(0.0005m, PaperPnl.Calculate(WonLong(), [Funding(Opened, 0.0001m, 100m)]).Funding);

    [Fact]
    public void ASettlementAtTheMomentOfTheExitIsNotPaid() =>
        Assert.Equal(0m, PaperPnl.Calculate(WonLong(), [Funding(Closed, 0.0001m, 100m)]).Funding);

    [Fact]
    public void SettlementsBeforeTheEntryAndAfterTheExitAreIgnored()
    {
        var history = new[]
        {
            Funding(Opened.AddSeconds(-1), 0.0001m, 100m),
            Funding(Closed.AddSeconds(1), 0.0001m, 100m),
        };

        Assert.Equal(0m, PaperPnl.Calculate(WonLong(), history).Funding);
    }

    [Fact]
    public void ASettlementWithoutAMarkPriceIsTakenAtTheEntryPrice() =>
        Assert.Equal(0.0005m, PaperPnl.Calculate(WonLong(), [Funding(Opened.AddHours(5), 0.0001m, 0m)]).Funding);

    [Fact]
    public void TheExchangeCostIsTheFeesPlusTheFunding()
    {
        var pnl = PaperPnl.Calculate(WonLong(), [Funding(Opened.AddHours(5), 0.0001m, 101m)]);

        Assert.Equal(0.00404m + 0.000505m, pnl.ExchangeCost);
    }

    [Fact]
    public void TheNetResultIsTheGrossMinusTheExchangeCost()
    {
        var pnl = PaperPnl.Calculate(WonLong(), [Funding(Opened.AddHours(5), 0.0001m, 101m)]);

        Assert.Equal(0.10m - 0.00404m - 0.000505m, pnl.Net);
    }

    [Fact]
    public void ReceivedFundingRaisesTheNetResult()
    {
        var pnl = PaperPnl.Calculate(WonLong(), [Funding(Opened.AddHours(5), -0.0001m, 101m)]);

        Assert.Equal(0.10m - 0.00404m + 0.000505m, pnl.Net);
        Assert.True(pnl.ExchangeCost < pnl.Fees);
    }

    [Fact]
    public void RMultipleCountsTheMoveInUnitsOfTheRiskTaken()
    {
        // Entry 100, stop 98: a risk of 2. Closed at 102 is +1 R, at 104 is +2 R.
        Assert.Equal(1m, PaperPnl.Calculate(WonLong(), []).RMultiple);

        var bigWin = PaperTestData.Opened(PaperTestData.Long());
        bigWin.Observe(104m, Closed);
        Assert.Equal(2m, PaperPnl.Calculate(bigWin, []).RMultiple);
    }

    [Fact]
    public void RMultipleOfAStopIsMinusOne()
    {
        var trade = PaperTestData.Opened(PaperTestData.Short());
        trade.Observe(102m, Closed);

        Assert.Equal(-1m, PaperPnl.Calculate(trade, []).RMultiple);
    }

    [Fact]
    public void ATradeThatIsStillOpenHasNoResult() =>
        Assert.Throws<InvalidOperationException>(() => PaperPnl.Calculate(PaperTestData.Opened(PaperTestData.Long()), []));
}
