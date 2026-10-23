using Infrastructure.Exchange.Binance;
using Binance.Net.Enums;
using Domain.Enums;
using Domain.Services;

namespace Infrastructure.UnitTests;

public class BinancePeriodIntervalsTests
{
    [Theory]
    [InlineData(TimeFrameEnum._5M, PeriodInterval.FiveMinutes)]
    [InlineData(TimeFrameEnum._15M, PeriodInterval.FifteenMinutes)]
    [InlineData(TimeFrameEnum._30M, PeriodInterval.ThirtyMinutes)]
    [InlineData(TimeFrameEnum._1H, PeriodInterval.OneHour)]
    [InlineData(TimeFrameEnum._2H, PeriodInterval.TwoHour)]
    [InlineData(TimeFrameEnum._4H, PeriodInterval.FourHour)]
    [InlineData(TimeFrameEnum._6H, PeriodInterval.SixHour)]
    [InlineData(TimeFrameEnum._12H, PeriodInterval.TwelveHour)]
    [InlineData(TimeFrameEnum._1D, PeriodInterval.OneDay)]
    public void MapsSupportedTimeFrames(TimeFrameEnum timeFrame, PeriodInterval expected)
    {
        Assert.True(BinancePeriodIntervals.TryMap(timeFrame, out var period));
        Assert.Equal(expected, period);
    }

    [Theory]
    [InlineData(TimeFrameEnum._1S)]
    [InlineData(TimeFrameEnum._1M)]
    [InlineData(TimeFrameEnum._3M)]
    [InlineData(TimeFrameEnum._8H)]
    public void RejectsTimeFramesBinanceStatisticsDoNotOffer(TimeFrameEnum timeFrame)
    {
        Assert.False(BinancePeriodIntervals.TryMap(timeFrame, out _));
    }

    [Fact]
    public void EveryTimeFrameIsEitherMappedOrExplicitlyUnsupported()
    {
        var unsupported = new[] { TimeFrameEnum._1S, TimeFrameEnum._1M, TimeFrameEnum._3M, TimeFrameEnum._8H };

        foreach (var timeFrame in Enum.GetValues<TimeFrameEnum>())
            Assert.Equal(!unsupported.Contains(timeFrame), BinancePeriodIntervals.TryMap(timeFrame, out _));
    }

    [Fact]
    public void TheDomainRuleForOpenInterestAndTheBinanceMappingAgree()
    {
        // MarketDataSupport is what the admin API validates against; this mapping is what the worker really sends to Binance.
        // If they drift apart, an accepted configuration would fail at run time (or a valid one be refused).
        var mapped = Enum.GetValues<TimeFrameEnum>().Where(t => BinancePeriodIntervals.TryMap(t, out _));

        Assert.Equal(mapped, MarketDataSupport.GetTimeFrames(ExchangeEnum.Binance, MarketTypeEnum.Futures, MarketDataTypeEnum.OpenInterest));
    }

    [Fact]
    public void DifferentTimeFramesNeverShareAPeriod()
    {
        var periods = Enum.GetValues<TimeFrameEnum>()
            .Where(t => BinancePeriodIntervals.TryMap(t, out _))
            .Select(t => { BinancePeriodIntervals.TryMap(t, out var p); return p; })
            .ToList();

        Assert.Equal(periods.Count, periods.Distinct().Count());
    }
}
