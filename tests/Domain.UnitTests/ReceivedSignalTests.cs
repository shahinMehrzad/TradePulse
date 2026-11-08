using Domain.Entities;
using Domain.Enums;

namespace Domain.UnitTests;

public class ReceivedSignalTests
{
    private static readonly DateTime ReceivedAt = PaperTestData.Now;

    [Fact]
    public void ASignalThatWasTakenHasNoRejection()
    {
        var received = ReceivedSignal.From(PaperTestData.Long(), ReceivedAt, rejection: null);

        Assert.True(received.WasTaken);
        Assert.Null(received.Rejection);
    }

    [Fact]
    public void ARefusedSignalKeepsTheReason()
    {
        var received = ReceivedSignal.From(PaperTestData.Long(), ReceivedAt, SignalRejectionEnum.PriceMovedTooFar);

        Assert.False(received.WasTaken);
        Assert.Equal(SignalRejectionEnum.PriceMovedTooFar, received.Rejection);
    }

    [Fact]
    public void ItKeepsEverythingTheSignalSaid()
    {
        var signal = PaperTestData.Short(price: 100, stop: 102, target: 96, leverage: 3, maxDuration: TimeSpan.FromHours(48));

        var received = ReceivedSignal.From(signal, ReceivedAt, null);

        Assert.Equal(signal.SignalId, received.SignalId);
        Assert.Equal("S1", received.Strategy);
        Assert.Equal(MarketTypeEnum.Futures, received.Market);
        Assert.Equal("BTC/USDT", received.Symbol);
        Assert.Equal(PositionSideEnum.Short, received.Side);
        Assert.Equal(3m, received.Leverage);
        Assert.Equal((96m, 102m, 100m), (received.TakeProfit, received.StopLoss, received.SignalPrice));
        Assert.Equal(TimeFrameEnum._4H, received.TimeFrame);
        Assert.Equal(PaperTestData.CandleOpen, received.CandleOpenTime);
        Assert.Equal(PaperTestData.CandleClose, received.CandleCloseTime);
        Assert.Equal(172_800, received.MaxDurationSeconds);
        Assert.Equal(ReceivedAt, received.ReceivedAt);
    }

    [Fact]
    public void ASignalWithoutADurationKeepsNone() =>
        Assert.Null(ReceivedSignal.From(PaperTestData.Long(), ReceivedAt, null).MaxDurationSeconds);

    [Fact]
    public void ItCanBecomeTheSignalAgain()
    {
        var signal = PaperTestData.Long(maxDuration: TimeSpan.FromHours(24));

        Assert.Equal(signal, ReceivedSignal.From(signal, ReceivedAt, null).ToTradeSignal());
    }

    [Fact]
    public void TheSignalItBecomesHasUtcTimesEvenWhenTheDatabaseGaveThemBackWithoutAKind()
    {
        var received = ReceivedSignal.From(PaperTestData.Long(), ReceivedAt, null);
        received.CandleOpenTime = DateTime.SpecifyKind(received.CandleOpenTime, DateTimeKind.Unspecified);
        received.CandleCloseTime = DateTime.SpecifyKind(received.CandleCloseTime, DateTimeKind.Unspecified);
        received.ValidUntil = DateTime.SpecifyKind(received.ValidUntil, DateTimeKind.Unspecified);

        var signal = received.ToTradeSignal();

        Assert.Equal(DateTimeKind.Utc, signal.CandleOpenTime.Kind);
        Assert.Equal(DateTimeKind.Utc, signal.CandleCloseTime.Kind);
        Assert.Equal(DateTimeKind.Utc, signal.ValidUntil.Kind);
    }
}
