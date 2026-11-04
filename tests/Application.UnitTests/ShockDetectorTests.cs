using Application.Research.Watch;
using Domain.Enums;
using static Application.UnitTests.ShockTape;

namespace Application.UnitTests;

public class ShockDetectorTests
{
    private static ShockDetector NewDetector(int listing = 7) => new(listing, Options);

    [Fact]
    public void AQuietMarketGivesNoSignals()
    {
        var signals = Feed(NewDetector(), 0, 400);

        Assert.Empty(signals);
    }

    [Fact]
    public void TooFewSecondsToKnowWhatIsUsualGiveNoSignals()
    {
        var detector = NewDetector();

        var signals = Feed(detector, 0, 90, second => Price(second, at: 50, jump: 0.02m));

        Assert.Empty(signals);
        Assert.False(detector.IsWarm);
    }

    [Fact]
    public void AJumpIsAnAlertStartingAtItsFirstSecond()
    {
        var detector = NewDetector(7);

        var signals = Feed(detector, 0, 200, second => Price(second, at: 150, jump: 0.01m));

        var started = Assert.Single(signals, s => s.Kind == ShockSignalKind.Started);
        var alert = started.Alert;
        Assert.True(detector.IsWarm);
        Assert.Equal(7, alert.SymbolListingId);
        Assert.Equal(Start.AddSeconds(150), alert.TriggeredAt);
        Assert.Equal(DateTimeKind.Utc, alert.TriggeredAt.Kind);
        Assert.Equal(EventDirectionEnum.Up, alert.Direction);
        Assert.Equal(AlertLevelEnum.Attention, alert.Level);   // one level: how big the move is shows in its z
        Assert.Equal(Options.WindowSeconds, alert.WindowSeconds);
        Assert.InRange(alert.Move, 0.0095m, 0.0105m);
        Assert.True(alert.ZScore >= (decimal)Options.MinZScore);
        Assert.Equal(Options.SuddenVersion, alert.RuleVersion);
        Assert.Equal(Wobble(140), alert.StartPrice);
        Assert.Equal(Price(150, at: 150, jump: 0.01m), alert.EndPrice);
    }

    [Fact]
    public void AFallIsADownAlertWithANegativeMoveAndZScore()
    {
        var signals = Feed(NewDetector(), 0, 200, second => Price(second, at: 150, jump: -0.01m));

        var alert = Assert.Single(signals, s => s.Kind == ShockSignalKind.Started).Alert;
        Assert.Equal(EventDirectionEnum.Down, alert.Direction);
        Assert.InRange(alert.Move, -0.0105m, -0.0095m);
        Assert.True(alert.ZScore < 0);
    }

    [Fact]
    public void TheAlertIsRaisedTheMomentTheSecondIsCompleteNotLater()
    {
        var detector = NewDetector();
        var signals = Feed(detector, 0, 150);

        // The jump is in second 150; it is complete, and judged, when the first trade of second 151 arrives.
        detector.Add(Trade(150, Price(150, at: 150, jump: 0.01m)), signals);
        Assert.Empty(signals);

        detector.Add(Trade(151, Price(151, at: 150, jump: 0.01m)), signals);
        Assert.Single(signals);
    }

    [Fact]
    public void AMoveThatIsNotUnusualEnoughIsNotAlertedHoweverBigItLooks()
    {
        // 0.4 % is about 13 times the usual size of a move over the window here, and the rule asks for 20.
        var signals = Feed(NewDetector(), 0, 200, second => Price(second, at: 150, jump: 0.004m));

        Assert.Empty(signals);
    }

    [Fact]
    public void TheSameMoveIsAnAlertOnceTheRuleAsksForLess()
    {
        var detector = new ShockDetector(7, Options with { MinZScore = 8 });

        var signals = Feed(detector, 0, 200, second => Price(second, at: 150, jump: 0.004m));

        Assert.Single(signals, s => s.Kind == ShockSignalKind.Started);
    }

    [Fact]
    public void AMoveUnderTheMinimumIsNotAlertedHoweverUnusualItIs()
    {
        // About 3 times the usual size of a move, which the rule here accepts (it asks for 2), but under the smallest move worth knowing about.
        var detector = new ShockDetector(7, Options with { MinZScore = 2 });

        var signals = Feed(detector, 0, 300, second => Price(second, at: 150, jump: 0.001m));

        Assert.Empty(signals);
    }

    [Fact]
    public void TheAlertClosesAfterTheMergeGapWithItsLargestMove()
    {
        var detector = NewDetector();

        var signals = Feed(detector, 0, 400, second => Price(second, at: 150, jump: 0.01m));

        Assert.Equal([ShockSignalKind.Started, ShockSignalKind.Closed], signals.Select(s => s.Kind));
        var closed = signals[1].Alert;
        Assert.Equal(signals[0].Alert.TriggeredAt, closed.TriggeredAt);
        Assert.InRange(closed.Move, 0.0095m, 0.0105m);
        Assert.Equal(AlertLevelEnum.Attention, closed.Level);
        // The jump joins the baseline the moment it is over, so the seconds that still hold it in their window are no longer unusual.
        Assert.Equal(1, closed.FlaggedSeconds);
    }

    [Fact]
    public void ASecondJumpInsideTheMergeGapIsTheSameAlert()
    {
        var price = (int second) => Price(second, at: 150, jump: 0.01m) * (second >= 190 ? 0.98m : 1m);

        // The first jump is still in the baseline when the second comes, so the second is judged against a restless hour: the rule here asks for 6.
        var signals = Feed(new ShockDetector(7, Options with { MinZScore = 6 }), 0, 500, price);

        Assert.Single(signals, s => s.Kind == ShockSignalKind.Started);
        var closed = Assert.Single(signals, s => s.Kind == ShockSignalKind.Closed).Alert;
        Assert.InRange(closed.Move, -0.025m, -0.015m); // the fall of 2 % + 1 % undone is the largest move of the alert
    }

    [Fact]
    public void QuietTimeWithoutTradesClosesTheAlertOnTime()
    {
        var detector = NewDetector();
        var signals = Feed(detector, 0, 200, second => Price(second, at: 150, jump: 0.01m));
        Assert.DoesNotContain(signals, s => s.Kind == ShockSignalKind.Closed);

        detector.Advance(Start.AddSeconds(400), signals);

        Assert.Contains(signals, s => s.Kind == ShockSignalKind.Closed);
    }

    [Fact]
    public void AdvanceLeavesTheLastSecondsOpenSoATradeStillOnItsWayIsNotLost()
    {
        var detector = NewDetector();
        var signals = Feed(detector, 0, 150);

        detector.Advance(Start.AddSeconds(151), signals);
        detector.Add(Trade(150, Price(150, at: 150, jump: 0.01m)), signals);

        Assert.Equal(0, detector.LateTrades);
    }

    [Fact]
    public void ATradeOlderThanTheSecondBeingBuiltIsLeftOutAndCounted()
    {
        var detector = NewDetector();
        var signals = Feed(detector, 0, 10);

        detector.Add(Trade(5, 1000m), signals);

        Assert.Equal(1, detector.LateTrades);
        Assert.Empty(signals);
    }

    [Fact]
    public void ASecondWithoutTradesKeepsThePriceAndHasNoVolume()
    {
        var detector = NewDetector();
        var signals = new List<ShockSignal>();
        for (var second = 0; second < 150; second++)
            detector.Add(Trade(second, Wobble(second)), signals);

        // Nothing traded for 20 seconds, then the price is 1 % higher: the jump counts as one move, not as twenty.
        for (var second = 170; second < 200; second++)
            detector.Add(Trade(second, Wobble(second) * 1.01m), signals);

        var alert = Assert.Single(signals, s => s.Kind == ShockSignalKind.Started).Alert;
        Assert.Equal(Start.AddSeconds(170), alert.TriggeredAt);
    }

    [Fact]
    public void TheVolumeIsWhatWasTradedInTheWindow()
    {
        var signals = Feed(NewDetector(), 0, 200, second => Price(second, at: 150, jump: 0.01m));

        var alert = Assert.Single(signals, s => s.Kind == ShockSignalKind.Started).Alert;
        Assert.Equal(2m * Options.WindowSeconds, alert.Volume);
    }

    [Fact]
    public void ALongGapStartsItOverSoTheOldBaselineIsNotTrustedAcrossIt()
    {
        var detector = NewDetector();
        var signals = Feed(detector, 0, 150);
        Assert.True(detector.IsWarm);

        // An hour later the price is 1 % up: unknown what happened between, and the baseline is gone.
        for (var second = 4000; second < 4100; second++)
            detector.Add(Trade(second, Price(second, at: 4000, jump: 0.01m)), signals);

        Assert.Empty(signals);
        Assert.False(detector.IsWarm);
    }

    [Fact]
    public void TheSameTradesAlwaysGiveTheSameSignals()
    {
        var first = Feed(NewDetector(), 0, 400, second => Price(second, at: 150, jump: 0.01m));
        var second = Feed(NewDetector(), 0, 400, second => Price(second, at: 150, jump: 0.01m));

        Assert.Equal(first.Select(s => (s.Kind, s.Alert.TriggeredAt, s.Alert.Move, s.Alert.ZScore)), second.Select(s => (s.Kind, s.Alert.TriggeredAt, s.Alert.Move, s.Alert.ZScore)));
    }

    // The tape's seconds move by about 0.0095 % (see ShockTape.Wobble).
    private const double TapeSecondVolatility = 9.5e-5;

    [Fact]
    public void WithASeedItJudgesAfterTheWindowNotAfterTheWholeBaseline()
    {
        var seeded = NewDetector();
        seeded.Seed(TapeSecondVolatility);
        var unseeded = NewDetector();

        var withSeed = Feed(seeded, 0, 40, second => Price(second, at: 25, jump: 0.01m));
        var withoutSeed = Feed(unseeded, 0, 40, second => Price(second, at: 25, jump: 0.01m));

        Assert.Single(withSeed, s => s.Kind == ShockSignalKind.Started);
        Assert.Empty(withoutSeed);
    }

    [Fact]
    public void ASeededDetectorIsSilentUntilTheWindowIsFilled()
    {
        var detector = NewDetector();
        detector.Seed(TapeSecondVolatility);

        var signals = Feed(detector, 0, 5, second => Price(second, at: 2, jump: 0.01m));

        Assert.Empty(signals);
        Assert.False(detector.IsWarm);
    }

    [Fact]
    public void TheSeedIsReplacedByRealSecondsOnceTheBaselineIsFull()
    {
        var detector = NewDetector();
        detector.Seed(TapeSecondVolatility);

        Feed(detector, 0, 50);
        Assert.True(detector.IsWarm);
        Assert.False(detector.IsSeedReplaced);

        Feed(detector, 50, 150);
        Assert.True(detector.IsSeedReplaced);
    }

    [Fact]
    public void ASeedThatIsTooHighMakesTheDetectorLessSensitiveOnlyUntilRealSecondsTakeOver()
    {
        // An estimate ten times the real size: a 1 % move is not unusual against it (about 3 times, the rule asks for 20), so the early jump
        // passes unnoticed.
        var detector = NewDetector();
        detector.Seed(TapeSecondVolatility * 10);

        var early = Feed(detector, 0, 60, second => Price(second, at: 30, jump: 0.01m));
        Assert.Empty(early);

        // Once the baseline is made of real seconds the same kind of jump is caught.
        var late = Feed(detector, 60, 400, second => Price(second, at: 300, jump: 0.01m) * (second >= 30 ? 1.01m : 1m));
        Assert.Single(late, s => s.Kind == ShockSignalKind.Started);
    }

    [Fact]
    public void ASeedMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewDetector().Seed(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NewDetector().Seed(-1e-4));
    }

    [Fact]
    public void ASeedSurvivesALongGapSoTheWatchCanJudgeAgainSoonAfter()
    {
        var detector = NewDetector();
        detector.Seed(TapeSecondVolatility);
        Feed(detector, 0, 150);

        var signals = new List<ShockSignal>();
        for (var second = 4000; second < 4040; second++)
            detector.Add(Trade(second, Price(second, at: 4025, jump: 0.01m)), signals);

        Assert.Single(signals, s => s.Kind == ShockSignalKind.Started);
    }

    [Fact]
    public void TheVersionsNameTheTwoChecksSoADifferentRuleIsADifferentVersion()
    {
        Assert.NotEqual(Options.SuddenVersion, (Options with { MinZScore = 8 }).SuddenVersion);
        Assert.NotEqual(Options.SuddenVersion, (Options with { WindowSeconds = 60 }).SuddenVersion);
        Assert.NotEqual(Options.GradualVersion, (Options with { GradualMinZScore = 5 }).GradualVersion);
        Assert.NotEqual(Options.GradualVersion, (Options with { GradualWindowMinutes = 30 }).GradualVersion);

        // Two alerts that start in the same second are two alerts: the store tells them apart by the version.
        Assert.NotEqual(Options.SuddenVersion, Options.GradualVersion);
        Assert.True(new ShockWatchOptions().SuddenVersion.Length <= 32 && new ShockWatchOptions().GradualVersion.Length <= 32, "the column is 32 characters");
    }

    // ---- the gradual check -------------------------------------------------------------------------------------------------

    /// <summary>A short rule for the made-up tape: a 5 minute window, and a day of 2 hours.</summary>
    private static readonly ShockWatchOptions GradualOptions = Options with { GradualWindowMinutes = 5, DayBaselineMinutes = 120, GradualMergeGap = TimeSpan.FromMinutes(10) };

    private const int HourAndAMinute = 61 * 60;

    /// <summary>The wobbling price that slides by <paramref name="slide"/> (-0.005 is -0.5 %) over <paramref name="over"/> seconds from <paramref name="at"/>.</summary>
    private static decimal Sliding(int second, int at, decimal slide, int over = 300) =>
        Wobble(second) * (1 + slide * Math.Clamp((second - at) / (decimal)over, 0m, 1m));

    [Fact]
    public void ASlideTooSlowForTheSuddenCheckIsFoundByTheGradualOne()
    {
        var detector = new ShockDetector(7, GradualOptions);

        var signals = Feed(detector, 0, HourAndAMinute + 900, second => Sliding(second, at: HourAndAMinute, slide: -0.005m));

        var started = Assert.Single(signals, s => s.Kind == ShockSignalKind.Started).Alert;
        Assert.Equal(GradualOptions.GradualVersion, started.RuleVersion);
        Assert.Equal(300, started.WindowSeconds);
        Assert.Equal(EventDirectionEnum.Down, started.Direction);
        Assert.True(Math.Abs(started.ZScore) >= (decimal)GradualOptions.GradualMinZScore);
        Assert.True(started.Move < 0 && started.StartPrice > started.EndPrice);
        Assert.InRange(started.TriggeredAt, Start.AddSeconds(HourAndAMinute), Start.AddSeconds(HourAndAMinute + 600));
        Assert.DoesNotContain(signals, s => s.Alert.RuleVersion == GradualOptions.SuddenVersion);
    }

    [Fact]
    public void AJumpIsSeenByBothChecksAsTwoAlerts()
    {
        var detector = new ShockDetector(7, GradualOptions);

        var signals = Feed(detector, 0, HourAndAMinute + 400, second => Price(second, at: HourAndAMinute, jump: 0.01m));

        var versions = signals.Where(s => s.Kind == ShockSignalKind.Started).Select(s => s.Alert.RuleVersion).Order().ToList();
        Assert.Equal(new[] { GradualOptions.GradualVersion, GradualOptions.SuddenVersion }.Order(), versions);
    }

    [Fact]
    public void TheGradualCheckStaysSilentUntilItHasAnHourToCompareWith()
    {
        var detector = new ShockDetector(7, GradualOptions);

        var signals = Feed(detector, 0, 40 * 60, second => Sliding(second, at: 30 * 60, slide: -0.01m));

        Assert.DoesNotContain(signals, s => s.Alert.RuleVersion == GradualOptions.GradualVersion);
    }

    [Fact]
    public void MinutesFromTheStoredCandlesLetTheGradualCheckJudgeFromTheStart()
    {
        var detector = new ShockDetector(7, GradualOptions);
        var firstMinute = (long)(Start - DateTime.UnixEpoch).TotalMinutes;
        detector.SeedMinutes(Enumerable.Range(1, 120).Select(i => new MinuteClose(firstMinute - i, Wobble(-i * 60 + 59))).Reverse());

        // The slide begins with the first trade: without the candles there would be nothing to compare it with for an hour.
        var signals = Feed(detector, 0, 900, second => Sliding(second, at: 0, slide: -0.005m));

        Assert.Contains(signals, s => s.Kind == ShockSignalKind.Started && s.Alert.RuleVersion == GradualOptions.GradualVersion);
    }

    [Fact]
    public void MinutesGivenAfterTheFirstTradeAreLeftOut()
    {
        var detector = new ShockDetector(7, GradualOptions);
        Feed(detector, 0, 5);
        var firstMinute = (long)(Start - DateTime.UnixEpoch).TotalMinutes;
        detector.SeedMinutes(Enumerable.Range(1, 120).Select(i => new MinuteClose(firstMinute - i, 100m)).Reverse());

        var signals = Feed(detector, 5, 900, second => Sliding(second, at: 5, slide: -0.005m));

        Assert.DoesNotContain(signals, s => s.Alert.RuleVersion == GradualOptions.GradualVersion);
    }

    [Fact]
    public void AQuietHourAfterANoisyDayDoesNotMakeASmallMoveLookHuge()
    {
        // Two hours of a restless price, then an hour of a calm one, then a slide of 0.3 %: against the calm hour alone that is huge, against
        // what the day usually does it is not.
        Func<int, decimal> price = second =>
        {
            var wobble = Wobble(second) - 100m;
            var noisy = second < 2 * 3600;
            var restless = 100m + wobble * (noisy ? 30m : 1m);
            return restless * (1 + -0.003m * Math.Clamp((second - 3 * 3600) / 300m, 0m, 1m));
        };
        var withDay = GradualOptions with { DayBaselineMinutes = 180 };
        var withoutDay = withDay with { DayBaselineShare = 0 };

        var seenWithDay = Feed(new ShockDetector(7, withDay), 0, 3 * 3600 + 900, price);
        var seenWithoutDay = Feed(new ShockDetector(7, withoutDay), 0, 3 * 3600 + 900, price);

        Assert.DoesNotContain(seenWithDay, s => s.Alert.RuleVersion == withDay.GradualVersion);
        Assert.Contains(seenWithoutDay, s => s.Kind == ShockSignalKind.Started && s.Alert.RuleVersion == withoutDay.GradualVersion);
    }
}
