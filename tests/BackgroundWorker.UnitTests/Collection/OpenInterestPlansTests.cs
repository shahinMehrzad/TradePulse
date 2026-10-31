using BackgroundWorker.Collection.OpenInterests;
using BackgroundWorker.UnitTests.CandleSeries;
using Domain.Enums;
using Domain.ValueObjects;

namespace BackgroundWorker.UnitTests.Collection;

public class OpenInterestPlansTests
{
    private static CandleSeriesPlan Plan(
        MarketTypeEnum market = MarketTypeEnum.Futures, SeededSymbol symbol = SeededSymbol.BTCUSDT,
        TimeFrameEnum timeFrame = TimeFrameEnum._1H, bool openInterest = true, ExchangeEnum exchange = ExchangeEnum.Binance) =>
        TestPlans.Binance(market, symbol, timeFrame) with
        {
            Series = new Domain.ValueObjects.CandleSeries(exchange, market, symbol.Sym(), timeFrame),
            CollectOpenInterest = openInterest,
        };

    [Fact]
    public void OnlyTheSeriesTheAdminFlaggedAreSelected()
    {
        var flagged = Plan(timeFrame: TimeFrameEnum._4H);

        var selected = OpenInterestPlans.SelectForBinance([Plan(timeFrame: TimeFrameEnum._1H, openInterest: false), flagged]);

        Assert.Equal([flagged], selected);
    }

    [Fact]
    public void NothingFlaggedMeansNothingSelectedAndNothingReported()
    {
        var refused = new List<string>();

        var selected = OpenInterestPlans.SelectForBinance([Plan(openInterest: false)], (_, reason) => refused.Add(reason));

        Assert.Empty(selected);
        Assert.Empty(refused);
    }

    [Fact]
    public void AnotherExchangesSeriesAreNotThisWorkersBusiness()
    {
        var selected = OpenInterestPlans.SelectForBinance([Plan(exchange: (ExchangeEnum)99)]);

        Assert.Empty(selected);
    }

    [Theory]
    [InlineData(MarketTypeEnum.Spot, TimeFrameEnum._1H, "OpenInterest is not available on Binance Spot")]
    [InlineData(MarketTypeEnum.Futures, TimeFrameEnum._1M, "does not support the 1M time frame")]
    [InlineData(MarketTypeEnum.Futures, TimeFrameEnum._8H, "does not support the 8H time frame")]
    public void AFlagTheMarketCannotHonourIsReportedAndLeftOut(MarketTypeEnum market, TimeFrameEnum timeFrame, string reason)
    {
        var bad = Plan(market: market, timeFrame: timeFrame);
        var good = Plan(symbol: SeededSymbol.ETHUSDT, timeFrame: TimeFrameEnum._1D);
        var refused = new List<(CandleSeriesPlan Plan, string Reason)>();

        var selected = OpenInterestPlans.SelectForBinance([bad, good], (plan, why) => refused.Add((plan, why)));

        Assert.Equal([good], selected);   // one bad row never stops the others
        var (refusedPlan, why) = Assert.Single(refused);
        Assert.Equal(bad, refusedPlan);
        Assert.Contains(reason, why);
    }

    [Fact]
    public void ASeriesListedTwiceIsCollectedOnce()
    {
        var plan = Plan();

        Assert.Single(OpenInterestPlans.SelectForBinance([plan, plan]));
    }
}
