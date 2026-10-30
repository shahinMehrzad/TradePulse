using BackgroundWorker.Collection.MarketStatistics;
using BackgroundWorker.UnitTests.CandleSeries;
using Domain.Enums;
using Domain.ValueObjects;

namespace BackgroundWorker.UnitTests.Collection;

public class MarketStatisticsPlansTests
{
    private static CandleSeriesPlan Plan(
        MarketTypeEnum market = MarketTypeEnum.Futures, TimeFrameEnum timeFrame = TimeFrameEnum._1H, bool statistics = true) =>
        TestPlans.Binance(market, SymbolEnum.BTCUSDT, timeFrame) with { CollectMarketStatistics = statistics };

    [Theory]
    [InlineData(MarketDataTypeEnum.LongShortRatio)]
    [InlineData(MarketDataTypeEnum.TakerVolume)]
    public void OnlyTheSeriesFlaggedForMarketStatisticsAreSelected(MarketDataTypeEnum type)
    {
        var flagged = Plan(timeFrame: TimeFrameEnum._4H);

        var selected = MarketStatisticsPlans.SelectForBinance([Plan(statistics: false), flagged], type);

        Assert.Equal([flagged], selected);
    }

    [Fact]
    public void TheOpenInterestFlagAloneSelectsNothing()
    {
        var plan = Plan(statistics: false) with { CollectOpenInterest = true };

        Assert.Empty(MarketStatisticsPlans.SelectForBinance([plan], MarketDataTypeEnum.LongShortRatio));
    }

    [Theory]
    [InlineData(MarketTypeEnum.Spot, TimeFrameEnum._1H)]
    [InlineData(MarketTypeEnum.Futures, TimeFrameEnum._1M)]
    [InlineData(MarketTypeEnum.Futures, TimeFrameEnum._8H)]
    public void AFlagTheMarketCannotHonourIsReportedAndLeftOut(MarketTypeEnum market, TimeFrameEnum timeFrame)
    {
        var bad = Plan(market, timeFrame);
        var good = Plan(timeFrame: TimeFrameEnum._1D);
        var refused = new List<CandleSeriesPlan>();

        var selected = MarketStatisticsPlans.SelectForBinance([bad, good], MarketDataTypeEnum.TakerVolume, (plan, _) => refused.Add(plan));

        Assert.Equal([good], selected);   // one bad row never stops the others
        Assert.Equal([bad], refused);
    }
}
