using Application.Research;
using Domain.Enums;

namespace Application.UnitTests;

public class MarketEventDetectorTests
{
    // A short window keeps the made-up series short; the rule is the same.
    private static readonly MarketEventDetectorOptions Options = new() { Window = 100 };

    private static MarketEventDetector Detector => new(Options);

    [Fact]
    public void AQuietMarketHasNoEvents()
    {
        var bars = MinuteBars.Build(400);

        Assert.Empty(Detector.Detect(bars, symbolListingId: 1));
    }

    [Fact]
    public void TooFewCandlesToKnowWhatIsUsualHaveNoEvents()
    {
        var bars = MinuteBars.Build(Options.Window, MinuteBars.Jump(at: 50, each: 0.02m));

        Assert.Empty(Detector.Detect(bars, symbolListingId: 1));
    }

    [Fact]
    public void AJumpIsOneEventStartingAtItsFirstMinute()
    {
        var bars = MinuteBars.Build(400, MinuteBars.Jump(at: 200, each: 0.015m));

        var found = Assert.Single(Detector.Detect(bars, symbolListingId: 7));

        Assert.Equal(7, found.SymbolListingId);
        Assert.Equal(bars[200].OpenTime, found.StartedAt);
        Assert.Equal(EventDirectionEnum.Up, found.Direction);
        Assert.True(found.FiveMinuteReturn > 0.03m, "the whole move of the three candles, not the first one");
        Assert.True(found.ZScore >= (decimal)Options.MinZScore);
        Assert.True(found.VolumeRatio > 10m);
        Assert.True(found.FlaggedMinutes > 1);
        Assert.Equal(Options.Version, found.DetectorVersion);
    }

    [Fact]
    public void AFallIsADownEventWithANegativeReturnAndZScore()
    {
        var bars = MinuteBars.Build(400, MinuteBars.Jump(at: 200, each: -0.015m));

        var found = Assert.Single(Detector.Detect(bars, symbolListingId: 1));

        Assert.Equal(EventDirectionEnum.Down, found.Direction);
        Assert.True(found.FiveMinuteReturn < -0.03m);
        Assert.True(found.ZScore < 0);
    }

    [Fact]
    public void TheMoveAndTheZScoreComeFromTheSameMinuteTheOneWithTheLargestMove()
    {
        // The first candle is the most unusual (the baseline is still quiet); the third one has the largest 5 minute move.
        var bars = MinuteBars.Build(400, MinuteBars.Jump(at: 200, each: 0.015m));
        var singleCandle = MinuteBars.Build(400, new Dictionary<int, decimal> { [200] = 0.015m });

        var wholeJump = Assert.Single(Detector.Detect(bars, symbolListingId: 1));
        var firstCandleOnly = Assert.Single(Detector.Detect(singleCandle, symbolListingId: 1));

        Assert.True(wholeJump.FiveMinuteReturn > 2 * firstCandleOnly.FiveMinuteReturn);
        Assert.True(wholeJump.ZScore < firstCandleOnly.ZScore, "the baseline already holds the first candles of the jump");
        Assert.True(wholeJump.ZScore >= (decimal)Options.MinZScore, "the minute was flagged, so it passed the threshold");
    }

    [Fact]
    public void AMoveTooSmallToMatterIsNotAnEventHoweverQuietTheMarketWas()
    {
        // Far above the usual wobble, but under the minimum size.
        var bars = MinuteBars.Build(400, MinuteBars.Jump(at: 200, each: 0.002m));

        Assert.Empty(Detector.Detect(bars, symbolListingId: 1));
    }

    [Fact]
    public void JumpsHoursApartAreSeparateEvents()
    {
        var moves = MinuteBars.Jump(at: 200, each: 0.015m);
        foreach (var (index, move) in MinuteBars.Jump(at: 380, each: -0.015m))
            moves[index] = move;

        var found = Detector.Detect(MinuteBars.Build(600, moves), symbolListingId: 1);

        Assert.Equal([EventDirectionEnum.Up, EventDirectionEnum.Down], found.Select(e => e.Direction));
    }

    [Fact]
    public void EventsBeforeFromAreLeftOut()
    {
        var moves = MinuteBars.Jump(at: 200, each: 0.015m);
        foreach (var (index, move) in MinuteBars.Jump(at: 380, each: -0.015m))
            moves[index] = move;
        var bars = MinuteBars.Build(600, moves);

        var found = Assert.Single(Detector.Detect(bars, symbolListingId: 1, from: bars[300].OpenTime));

        Assert.Equal(EventDirectionEnum.Down, found.Direction);
    }

    [Fact]
    public void AHoleInTheDataIsNotAMove()
    {
        // One candle moves 3 % and the hour before it is missing: every five minutes that reach back over the hole are skipped,
        // and the ones after it no longer contain the move.
        var bars = MinuteBars.Build(400, new Dictionary<int, decimal> { [200] = 0.03m }, holeBefore: 200);

        Assert.Empty(Detector.Detect(bars, symbolListingId: 1));
    }

    [Fact]
    public void TheSameCandlesAlwaysGiveTheSameEvents()
    {
        var bars = MinuteBars.Build(400, MinuteBars.Jump(at: 200, each: 0.015m));

        var first = Assert.Single(Detector.Detect(bars, symbolListingId: 1));
        var second = Assert.Single(Detector.Detect(bars, symbolListingId: 1));

        Assert.Equal((first.StartedAt, first.FiveMinuteReturn, first.ZScore, first.VolumeRatio, first.FlaggedMinutes),
            (second.StartedAt, second.FiveMinuteReturn, second.ZScore, second.VolumeRatio, second.FlaggedMinutes));
    }

    [Fact]
    public void TheVersionNamesTheRuleSoADifferentRuleIsADifferentVersion()
    {
        Assert.NotEqual(Options.Version, (Options with { MinZScore = 8 }).Version);
        Assert.NotEqual(Options.Version, (Options with { MinFiveMinuteMove = 0.02 }).Version);
        Assert.True(Options.Version.Length <= 32, "the column is 32 characters");
    }
}
