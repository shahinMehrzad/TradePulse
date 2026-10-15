using Domain.Enums;

namespace Domain.UnitTests;

/// <summary>
/// The numeric value of <see cref="TimeFrameEnum"/> is the interval length in seconds. The worker, the table
/// names and the scheduler all rely on that, so it is part of the contract shared with other services.
/// </summary>
public class TimeFrameEnumTests
{
    [Theory]
    [InlineData(TimeFrameEnum._1S, 1)]
    [InlineData(TimeFrameEnum._1M, 60)]
    [InlineData(TimeFrameEnum._3M, 180)]
    [InlineData(TimeFrameEnum._5M, 300)]
    [InlineData(TimeFrameEnum._15M, 900)]
    [InlineData(TimeFrameEnum._30M, 1_800)]
    [InlineData(TimeFrameEnum._1H, 3_600)]
    [InlineData(TimeFrameEnum._2H, 7_200)]
    [InlineData(TimeFrameEnum._4H, 14_400)]
    [InlineData(TimeFrameEnum._6H, 21_600)]
    [InlineData(TimeFrameEnum._8H, 28_800)]
    [InlineData(TimeFrameEnum._12H, 43_200)]
    [InlineData(TimeFrameEnum._1D, 86_400)]
    public void ValueIsTheIntervalLengthInSeconds(TimeFrameEnum timeFrame, int seconds)
    {
        Assert.Equal(seconds, (int)timeFrame);
    }

    [Fact]
    public void EveryTimeFrameDividesOneDayOrIsOneDay()
    {
        // Candle boundaries are computed as multiples of the interval since the epoch, which only lines up
        // with the exchange's UTC boundaries for intervals that divide a day.
        foreach (var timeFrame in Enum.GetValues<TimeFrameEnum>())
            Assert.Equal(0, 86_400 % (int)timeFrame);
    }

    [Fact]
    public void ValuesAreUnique()
    {
        var values = Enum.GetValues<TimeFrameEnum>().Select(v => (int)v).ToList();

        Assert.Equal(values.Count, values.Distinct().Count());
    }
}
