using Domain.ValueObjects;

namespace Domain.UnitTests;

public class TimeRangeTests
{
    private static readonly DateTime Open = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Constructor_KeepsBothBounds()
    {
        var close = Open.AddHours(1).AddMilliseconds(-1);

        var range = new TimeRange(Open, close);

        Assert.Equal(Open, range.OpenTime);
        Assert.Equal(close, range.CloseTime);
    }

    [Fact]
    public void Constructor_AllowsCloseEqualToOpen()
    {
        var range = new TimeRange(Open, Open);

        Assert.Equal(range.OpenTime, range.CloseTime);
    }

    [Fact]
    public void Constructor_RejectsCloseBeforeOpen()
    {
        var ex = Assert.Throws<ArgumentException>(() => new TimeRange(Open, Open.AddTicks(-1)));

        Assert.Equal("closeTime", ex.ParamName);
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
        var ex = Assert.Throws<ArgumentException>(() => new TimeRange(Open, default));

        Assert.Equal("closeTime", ex.ParamName);
    }
}
