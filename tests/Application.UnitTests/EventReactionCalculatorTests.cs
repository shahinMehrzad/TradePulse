using Application.Research;
using Domain.Entities;

namespace Application.UnitTests;

public class EventReactionCalculatorTests
{
    private static readonly DateTime Started = MinuteBars.Start.AddHours(5);

    private static readonly EventReactionCalculator Calculator = new(new MarketEventDetectorOptions { Horizons = [3, 1, 2, 10] });

    private static MinuteBar Bar(int minutesAfter, decimal high, decimal low, decimal close) =>
        new(Started.AddMinutes(minutesAfter), high, low, close, 10m);

    private static readonly MarketEvent Event = new() { StartedAt = Started };

    [Fact]
    public void MeasuresEveryHorizonFromTheCloseOfTheFirstMinute()
    {
        IReadOnlyList<MinuteBar> bars =
        [
            Bar(-1, 100, 100, 100),
            Bar(0, 100, 100, 100),
            Bar(1, 101, 99, 100),
            Bar(2, 103, 98, 102),
            Bar(3, 106, 104, 105),
        ];

        var reactions = Calculator.Calculate(Event, bars);

        Assert.Equal([1, 2, 3], reactions.Select(r => r.HorizonMinutes));
        Assert.Equal([0m, 0.02m, 0.05m], reactions.Select(r => r.Return));
        Assert.Equal([0.01m, 0.03m, 0.06m], reactions.Select(r => r.MaxRise));
        Assert.Equal([-0.01m, -0.02m, -0.02m], reactions.Select(r => r.MaxFall));
    }

    [Fact]
    public void LeavesOutTheHorizonsThePriceDataDoesNotReach()
    {
        IReadOnlyList<MinuteBar> bars = [Bar(0, 100, 100, 100), Bar(1, 101, 99, 100)];

        var reactions = Calculator.Calculate(Event, bars);

        Assert.Equal(1, Assert.Single(reactions).HorizonMinutes);
    }

    [Fact]
    public void StopsAtAHoleInTheData()
    {
        // Minute 2 is missing: nothing after it can be said to follow the event.
        IReadOnlyList<MinuteBar> bars = [Bar(0, 100, 100, 100), Bar(1, 101, 99, 100), Bar(3, 120, 90, 110)];

        var reactions = Calculator.Calculate(Event, bars);

        Assert.Equal(1, Assert.Single(reactions).HorizonMinutes);
    }

    [Fact]
    public void AnEventThatIsNotInTheCandlesHasNoReactions()
    {
        IReadOnlyList<MinuteBar> bars = [Bar(5, 100, 100, 100)];

        Assert.Empty(Calculator.Calculate(Event, bars));
    }
}
