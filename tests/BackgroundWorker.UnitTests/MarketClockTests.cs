namespace BackgroundWorker.UnitTests;

public class MarketClockTests
{
    private static readonly TimeSpan Offset = TimeSpan.FromSeconds(3);

    private static DateTime Utc(int hour, int minute, int second, int millisecond = 0) =>
        new(2024, 3, 15, hour, minute, second, millisecond, DateTimeKind.Utc);

    [Fact]
    public void WaitsUntilTheNextBoundaryPlusTheOffset()
    {
        var delay = MarketClock.GetDelayUntilNextBoundary(TimeSpan.FromHours(1), Offset, Utc(10, 30, 0));

        Assert.Equal(TimeSpan.FromMinutes(30) + Offset, delay);
    }

    [Fact]
    public void AtExactlyABoundaryWaitsAFullInterval()
    {
        var delay = MarketClock.GetDelayUntilNextBoundary(TimeSpan.FromMinutes(15), Offset, Utc(10, 15, 0));

        Assert.Equal(TimeSpan.FromMinutes(15) + Offset, delay);
    }

    [Fact]
    public void JustBeforeABoundaryWaitsOnlyTheRemainder()
    {
        var delay = MarketClock.GetDelayUntilNextBoundary(TimeSpan.FromHours(4), Offset, Utc(7, 59, 59, 500));

        Assert.Equal(TimeSpan.FromMilliseconds(500) + Offset, delay);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(60)]
    [InlineData(240)]
    [InlineData(1440)]
    public void BoundariesAlignWithUtcClockTimes(int minutes)
    {
        var interval = TimeSpan.FromMinutes(minutes);
        var now = Utc(13, 37, 21, 123);

        var wake = now + MarketClock.GetDelayUntilNextBoundary(interval, TimeSpan.Zero, now);

        // Candle boundaries on Binance are UTC multiples of the interval since midnight.
        Assert.Equal(0, (wake - wake.Date).Ticks % interval.Ticks);
        Assert.True(wake > now);
        Assert.True(wake - now <= interval);
    }

    [Fact]
    public async Task RunsImmediatelyAndStopsCleanlyWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        var runs = 0;

        await MarketClock.RunEveryIntervalAsync(TimeSpan.FromHours(1), _ =>
        {
            runs++;
            cts.Cancel();
            return Task.CompletedTask;
        }, cts.Token);

        Assert.Equal(1, runs);
    }
}
