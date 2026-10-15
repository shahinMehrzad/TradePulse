using Domain.ValueObjects;

namespace UnitTests;

public class TimeRangeTests
{
    private static readonly DateTime Open = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Constructor_AcceptsValidRange()
    {
        var range = new TimeRange(Open, Open.AddHours(4));

        Assert.Equal(Open, range.OpenTime);
        Assert.Equal(Open.AddHours(4), range.CloseTime);
    }

    [Fact]
    public void Constructor_AcceptsEqualOpenAndCloseTime()
    {
        var range = new TimeRange(Open, Open);

        Assert.Equal(range.OpenTime, range.CloseTime);
    }

    [Fact]
    public void Constructor_RejectsDefaultOpenTime()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TimeRange(default, Open));

        Assert.Equal("openTime", ex.ParamName);
    }

    [Fact]
    public void Constructor_RejectsDefaultCloseTime()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TimeRange(Open, DateTime.MinValue));

        Assert.Equal("closeTime", ex.ParamName);
    }

    [Fact]
    public void Constructor_RejectsCloseTimeBeforeOpenTime()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TimeRange(Open, Open.AddMinutes(-1)));

        Assert.Equal("closeTime", ex.ParamName);
    }
}
