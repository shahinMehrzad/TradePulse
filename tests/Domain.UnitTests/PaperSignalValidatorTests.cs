using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;

namespace Domain.UnitTests;

public class PaperSignalValidatorTests
{
    private static readonly PaperStrategySettings Settings = PaperStrategySettings.Default;

    private static SignalRejectionEnum? Check(PaperSignal signal, decimal livePrice = 100, DateTime? now = null, PaperStrategySettings? settings = null, int open = 0) =>
        PaperSignalValidator.Validate(signal, settings ?? Settings, livePrice, now ?? PaperTestData.Now, open);

    [Fact]
    public void ALongAtItsOwnPriceIsTaken() => Assert.Null(Check(PaperTestData.Long()));

    [Fact]
    public void AShortAtItsOwnPriceIsTaken() => Assert.Null(Check(PaperTestData.Short()));

    [Fact]
    public void ADisabledStrategyIsRefusedBeforeAnythingElse() =>
        Assert.Equal(SignalRejectionEnum.StrategyDisabled,
            Check(PaperTestData.Long() with { SignalId = "wrong" }, settings: Settings with { Enabled = false }));

    [Fact]
    public void SpotIsRefused() =>
        Assert.Equal(SignalRejectionEnum.UnsupportedMarket, Check(PaperTestData.Long() with { Market = MarketTypeEnum.Spot }));

    [Fact]
    public void AnIdThatIsNotBuiltFromTheSignalIsRefused() =>
        Assert.Equal(SignalRejectionEnum.SignalIdMismatch, Check(PaperTestData.Long() with { SignalId = "S1:BTCUSDT:4h:2026-11-07T08:00Z:long" }));

    [Fact]
    public void AnIdBuiltFromTheCloseTimeInsteadOfTheOpenTimeIsRefused()
    {
        // The mistake the contract rules out: reading "candle time" as the time the candle closed.
        var signal = PaperTestData.Long();
        var closeBased = PaperSignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, PaperTestData.CandleClose, PositionSideEnum.Long);

        Assert.Equal(SignalRejectionEnum.SignalIdMismatch, Check(signal with { SignalId = closeBased }));
    }

    [Fact]
    public void ACloseTimeThatIsNotTheOpenTimePlusTheTimeFrameIsRefused() =>
        Assert.Equal(SignalRejectionEnum.InconsistentCandleTimes,
            Check(PaperTestData.Long() with { CandleCloseTime = PaperTestData.CandleOpen.AddHours(3) }));

    [Fact]
    public void LeverageAtTheLimitIsTaken() =>
        Assert.Null(Check(PaperTestData.Long(leverage: 20)));

    [Fact]
    public void LeverageAboveTheLimitIsRefused() =>
        Assert.Equal(SignalRejectionEnum.LeverageOutOfRange, Check(PaperTestData.Long(leverage: 21)));

    [Fact]
    public void LeverageBelowOneIsRefused() =>
        Assert.Equal(SignalRejectionEnum.LeverageOutOfRange, Check(PaperTestData.Long(leverage: 0.5m)));

    [Fact]
    public void ALongWithTheStopAboveThePriceIsRefused() =>
        Assert.Equal(SignalRejectionEnum.LevelsOnWrongSide, Check(PaperTestData.Long(stop: 101)));

    [Fact]
    public void ALongWithTheTargetBelowThePriceIsRefused() =>
        Assert.Equal(SignalRejectionEnum.LevelsOnWrongSide, Check(PaperTestData.Long(target: 99)));

    [Fact]
    public void AShortWithTheStopBelowThePriceIsRefused() =>
        Assert.Equal(SignalRejectionEnum.LevelsOnWrongSide, Check(PaperTestData.Short(stop: 99)));

    [Fact]
    public void AShortWithTheTargetAboveThePriceIsRefused() =>
        Assert.Equal(SignalRejectionEnum.LevelsOnWrongSide, Check(PaperTestData.Short(target: 101)));

    [Fact]
    public void AStopAtZeroIsRefused() =>
        Assert.Equal(SignalRejectionEnum.LevelsOnWrongSide, Check(PaperTestData.Long(stop: 0)));

    [Fact]
    public void ACandleThatClosesWithinTheClockSkewIsTaken() =>
        Assert.Null(Check(PaperTestData.Long(), now: PaperTestData.CandleClose - PaperSignalValidator.ClockSkew));

    [Fact]
    public void ACandleThatClosesBeyondTheClockSkewIsRefused() =>
        Assert.Equal(SignalRejectionEnum.SignalFromTheFuture,
            Check(PaperTestData.Long(), now: PaperTestData.CandleClose - PaperSignalValidator.ClockSkew - TimeSpan.FromSeconds(1)));

    [Fact]
    public void ASignalIsTakenUpToItsValidUntil() =>
        Assert.Null(Check(PaperTestData.Long(), now: PaperTestData.CandleClose.AddMinutes(45)));

    [Fact]
    public void ASignalIsRefusedAfterItsValidUntil() =>
        Assert.Equal(SignalRejectionEnum.SignalExpired,
            Check(PaperTestData.Long(), now: PaperTestData.CandleClose.AddMinutes(45).AddSeconds(1)));

    [Fact]
    public void TheSendersValidUntilCanBeStricterThanTheReceiversLimit() =>
        Assert.Equal(SignalRejectionEnum.SignalExpired,
            Check(PaperTestData.Long() with { ValidUntil = PaperTestData.CandleClose.AddSeconds(5) }, now: PaperTestData.CandleClose.AddSeconds(6)));

    [Fact]
    public void ASignalAtTheAgeLimitIsTaken() =>
        Assert.Null(Check(PaperTestData.Long() with { ValidUntil = PaperTestData.CandleClose.AddHours(5) }, now: PaperTestData.CandleClose.AddMinutes(60)));

    [Fact]
    public void ASignalOlderThanTheAgeLimitIsRefusedEvenIfTheSenderStillCallsItValid() =>
        Assert.Equal(SignalRejectionEnum.SignalTooOld,
            Check(PaperTestData.Long() with { ValidUntil = PaperTestData.CandleClose.AddHours(5) }, now: PaperTestData.CandleClose.AddMinutes(60).AddSeconds(1)));

    [Fact]
    public void AStrategyThatNeedsSecondsGetsItsOwnAgeLimit() =>
        Assert.Equal(SignalRejectionEnum.SignalTooOld,
            Check(PaperTestData.Long(), settings: Settings with { MaxSignalAge = TimeSpan.FromSeconds(1) }));

    public static TheoryData<decimal> LongPricesAtOrPastALevel => new() { 98m, 97m, 104m, 110m };

    [Theory]
    [MemberData(nameof(LongPricesAtOrPastALevel))]
    public void ALongIsRefusedWhenThePriceIsAlreadyAtOrPastALevel(decimal live) =>
        Assert.Equal(SignalRejectionEnum.PriceBeyondLevels, Check(PaperTestData.Long(), live));

    public static TheoryData<decimal> ShortPricesAtOrPastALevel => new() { 102m, 103m, 96m, 90m };

    [Theory]
    [MemberData(nameof(ShortPricesAtOrPastALevel))]
    public void AShortIsRefusedWhenThePriceIsAlreadyAtOrPastALevel(decimal live) =>
        Assert.Equal(SignalRejectionEnum.PriceBeyondLevels, Check(PaperTestData.Short(), live));

    // The stop is 2 away, so the default quarter of it is 0.5.
    [Fact]
    public void ALongTakesAMoveOfExactlyAQuarterOfTheStopDistanceUp() => Assert.Null(Check(PaperTestData.Long(), 100.5m));

    [Fact]
    public void ALongTakesAMoveOfExactlyAQuarterOfTheStopDistanceDown() => Assert.Null(Check(PaperTestData.Long(), 99.5m));

    [Fact]
    public void ALongRefusesAMoveJustOverAQuarterUp() =>
        Assert.Equal(SignalRejectionEnum.PriceMovedTooFar, Check(PaperTestData.Long(), 100.51m));

    [Fact]
    public void ALongRefusesAMoveJustOverAQuarterDown() =>
        Assert.Equal(SignalRejectionEnum.PriceMovedTooFar, Check(PaperTestData.Long(), 99.49m));

    [Fact]
    public void AShortTakesAMoveOfExactlyAQuarterEitherWay()
    {
        Assert.Null(Check(PaperTestData.Short(), 99.5m));
        Assert.Null(Check(PaperTestData.Short(), 100.5m));
    }

    [Fact]
    public void AShortRefusesAMoveJustOverAQuarterEitherWay()
    {
        Assert.Equal(SignalRejectionEnum.PriceMovedTooFar, Check(PaperTestData.Short(), 99.49m));
        Assert.Equal(SignalRejectionEnum.PriceMovedTooFar, Check(PaperTestData.Short(), 100.51m));
    }

    [Fact]
    public void TheAllowedMoveFollowsTheStopDistanceNotAFixedPercent()
    {
        // A stop 10 away allows a move of 2.5 (2.5 %), a stop 0.9 away only 0.225 (0.225 %).
        Assert.Null(Check(PaperTestData.Long(stop: 90, target: 130), 102.5m));
        Assert.Equal(SignalRejectionEnum.PriceMovedTooFar, Check(PaperTestData.Long(stop: 99.1m, target: 104), 100.23m));
    }

    [Fact]
    public void AStrategyWithALooserMoveLimitTakesWhatTheDefaultRefuses() =>
        Assert.Null(Check(PaperTestData.Long(), 101m, settings: Settings with { MaxMoveOfStopDistance = 0.6m }));

    [Fact]
    public void WithNoLimitAnyNumberOfOpenTradesIsTaken() => Assert.Null(Check(PaperTestData.Long(), open: 50));

    [Fact]
    public void WithALimitTheTradeThatWouldGoOverItIsRefused() =>
        Assert.Equal(SignalRejectionEnum.TooManyOpenTrades,
            Check(PaperTestData.Long(), settings: Settings with { MaxOpenPerSymbol = 1 }, open: 1));

    [Fact]
    public void WithALimitTheLastFreeSlotIsTaken() =>
        Assert.Null(Check(PaperTestData.Long(), settings: Settings with { MaxOpenPerSymbol = 2 }, open: 1));

    [Fact]
    public void TheFirstRuleThatFailsIsTheOneReported() =>
        // Too old and past a level: the age is checked first.
        Assert.Equal(SignalRejectionEnum.SignalTooOld,
            Check(PaperTestData.Long() with { ValidUntil = PaperTestData.CandleClose.AddDays(1) }, livePrice: 90, now: PaperTestData.CandleClose.AddHours(2)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ALivePriceThatIsNotPositiveIsABug(int livePrice) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Check(PaperTestData.Long(), livePrice));
}
