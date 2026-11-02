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
        Assert.Equal(AlertLevelEnum.Alarm, alert.Level);
        Assert.Equal(Options.WindowSeconds, alert.WindowSeconds);
        Assert.InRange(alert.Move, 0.0095m, 0.0105m);
        Assert.True(alert.ZScore >= (decimal)Options.MinZScore);
        Assert.Equal(Options.Version, alert.RuleVersion);
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
    public void AMoveBetweenAttentionAndAlarmIsAnAttention()
    {
        var signals = Feed(NewDetector(), 0, 200, second => Price(second, at: 150, jump: 0.004m));

        var alert = Assert.Single(signals, s => s.Kind == ShockSignalKind.Started).Alert;
        Assert.Equal(AlertLevelEnum.Attention, alert.Level);
    }

    [Fact]
    public void AnAttentionThatGrowsPastTheAlarmThresholdIsRaisedOnceNotStartedAgain()
    {
        var price = (int second) => Price(second, at: 150, jump: 0.004m) * (second >= 152 ? 1.004m : 1m);

        var signals = Feed(NewDetector(), 0, 200, price);

        Assert.Equal([ShockSignalKind.Started, ShockSignalKind.Escalated], signals.Select(s => s.Kind));
        Assert.Equal([AlertLevelEnum.Attention, AlertLevelEnum.Alarm], signals.Select(s => s.Alert.Level));
        Assert.Equal(signals[0].Alert.TriggeredAt, signals[1].Alert.TriggeredAt);
    }

    [Fact]
    public void AMoveBelowTheAttentionThresholdIsNotAlertedHoweverUnusualItIs()
    {
        // Far above the usual wobble (z is large), but under the smallest move worth knowing about.
        var signals = Feed(NewDetector(), 0, 300, second => Price(second, at: 150, jump: 0.001m));

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
        Assert.Equal(AlertLevelEnum.Alarm, closed.Level);
        // The jump joins the baseline the moment it is over, so the seconds that still hold it in their window are no longer unusual.
        Assert.Equal(1, closed.FlaggedSeconds);
    }

    [Fact]
    public void ASecondJumpInsideTheMergeGapIsTheSameAlert()
    {
        var price = (int second) => Price(second, at: 150, jump: 0.01m) * (second >= 190 ? 0.98m : 1m);

        var signals = Feed(NewDetector(), 0, 500, price);

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
        // An estimate ten times the real size: a 0.4 % move is not unusual against it, so the early jump passes unnoticed.
        var detector = NewDetector();
        detector.Seed(TapeSecondVolatility * 10);

        var early = Feed(detector, 0, 60, second => Price(second, at: 30, jump: 0.004m));
        Assert.Empty(early);

        // Once the baseline is made of real seconds the same kind of jump is caught.
        var late = Feed(detector, 60, 400, second => Price(second, at: 300, jump: 0.01m) * (second >= 30 ? 1.004m : 1m));
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
    public void TheVersionNamesTheRuleSoADifferentRuleIsADifferentVersion()
    {
        Assert.NotEqual(Options.Version, (Options with { MinZScore = 8 }).Version);
        Assert.NotEqual(Options.Version, (Options with { AttentionMove = 0.004 }).Version);
        Assert.NotEqual(Options.Version, (Options with { WindowSeconds = 60 }).Version);
        Assert.True(new ShockWatchOptions().Version.Length <= 32, "the column is 32 characters");
    }
}
