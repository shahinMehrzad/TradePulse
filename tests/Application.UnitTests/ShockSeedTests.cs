using Application.Research.Watch;
using Domain.Entities;

namespace Application.UnitTests;

public class ShockSeedTests
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private static List<Candle> Candles(int count, Func<int, decimal>? close = null, int? holeAt = null)
    {
        var bars = MinuteBars.Build(count);
        return bars.Select((bar, i) => MinuteBars.ToCandle(bar with
        {
            OpenTime = MinuteBars.Start.AddMinutes(i + (holeAt is { } h && i >= h ? 5 : 0)),
            Close = close?.Invoke(i) ?? bar.Close,
        })).ToList();
    }

    private static DateTime JustAfter(IReadOnlyList<Candle> candles) => candles[^1].TimeRange.OpenTime.AddMinutes(1);

    [Fact]
    public void ASecondIsASixtiethOfTheVarianceOfAMinute()
    {
        var candles = Candles(61);

        Assert.True(ShockSeed.TryEstimate(candles, JustAfter(candles), MaxAge, 30, out var volatility));

        // The made-up minutes move by about 0.04 % each, so a second moves by about 0.04 % / sqrt(60).
        Assert.InRange(volatility, 4.5e-5, 6.0e-5);
    }

    [Fact]
    public void TheOrderOfTheCandlesDoesNotMatter()
    {
        var candles = Candles(61);
        var shuffled = candles.AsEnumerable().Reverse().ToList();

        Assert.True(ShockSeed.TryEstimate(candles, JustAfter(candles), MaxAge, 30, out var inOrder));
        Assert.True(ShockSeed.TryEstimate(shuffled, JustAfter(candles), MaxAge, 30, out var reversed));
        Assert.Equal(inOrder, reversed);
    }

    [Fact]
    public void CandlesThatEndLongBeforeNowSayNothingAboutNow()
    {
        var candles = Candles(61);

        Assert.False(ShockSeed.TryEstimate(candles, JustAfter(candles).AddMinutes(30), MaxAge, 30, out var volatility));
        Assert.Equal(0, volatility);
    }

    [Fact]
    public void TooFewReturnsAreNotEnoughToEstimateFrom()
    {
        var candles = Candles(20);

        Assert.False(ShockSeed.TryEstimate(candles, JustAfter(candles), MaxAge, 30, out _));
        Assert.False(ShockSeed.TryEstimate([], DateTime.UtcNow, MaxAge, 30, out _));
    }

    [Fact]
    public void AHoleInTheCandlesIsLeftOutNotCountedAsAFiveMinuteMove()
    {
        var withHole = Candles(61, holeAt: 30);
        var without = Candles(61);

        Assert.True(ShockSeed.TryEstimate(withHole, JustAfter(withHole), MaxAge, 30, out var volatility));
        Assert.True(ShockSeed.TryEstimate(without, JustAfter(without), MaxAge, 30, out var whole));

        Assert.InRange(volatility / whole, 0.9, 1.1);
    }

    [Fact]
    public void ACompletelyFlatPriceHasNothingToEstimate()
    {
        var candles = Candles(61, close: _ => 100m);

        Assert.False(ShockSeed.TryEstimate(candles, JustAfter(candles), MaxAge, 30, out _));
    }
}
