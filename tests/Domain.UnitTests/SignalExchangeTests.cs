using Domain.Entities;
using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;

namespace Domain.UnitTests;

/// <summary>
/// The exchange of a signal. Only Binance exists today, so a second one is stood in for by a number that is not a member yet; what is tested
/// is the rule that holds for any other exchange: its ids carry its name and the ids of Binance stay as they were.
/// </summary>
public class SignalExchangeTests
{
    private const ExchangeEnum Another = (ExchangeEnum)2;

    private static TradeSignal OnAnother()
    {
        var binance = PaperTestData.Long();
        var id = SignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, PaperTestData.CandleOpen, PositionSideEnum.Long, Another);

        return binance with { SignalId = id, Exchange = Another };
    }

    [Fact]
    public void ASignalThatDoesNotSayTakesBinance() => Assert.Equal(ExchangeEnum.Binance, PaperTestData.Long().Exchange);

    [Fact]
    public void TheIdOfBinanceIsAsItWasBeforeExchangesExisted() =>
        Assert.Equal("S1:BTCUSDT:4h:2026-11-07T04:00Z:long", PaperTestData.Long().SignalId);

    [Fact]
    public void TheIdOfAnotherExchangeCarriesItsNameInFront()
    {
        var binance = SignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, PaperTestData.CandleOpen, PositionSideEnum.Long);
        var another = SignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, PaperTestData.CandleOpen, PositionSideEnum.Long, Another);

        Assert.NotEqual(binance, another);
        Assert.EndsWith(":" + binance, another);
    }

    [Fact]
    public void ASignalOnAnotherExchangeIsTakenWhenItsIdSaysSo() =>
        Assert.Null(SignalValidator.Validate(OnAnother(), SignalStrategySettings.Default, 100m, PaperTestData.Now, 0));

    [Fact]
    public void ASignalOnAnotherExchangeWithTheIdOfBinanceIsRefused()
    {
        var signal = OnAnother() with { SignalId = PaperTestData.Long().SignalId };

        Assert.Equal(SignalRejectionEnum.SignalIdMismatch, SignalValidator.Validate(signal, SignalStrategySettings.Default, 100m, PaperTestData.Now, 0));
    }

    [Fact]
    public void ASignalOnBinanceWithTheNameOfAnotherExchangeInItsIdIsRefused()
    {
        var signal = PaperTestData.Long() with { SignalId = OnAnother().SignalId };

        Assert.Equal(SignalRejectionEnum.SignalIdMismatch, SignalValidator.Validate(signal, SignalStrategySettings.Default, 100m, PaperTestData.Now, 0));
    }

    [Fact]
    public void ThePaperTradeAndTheStoredSignalKeepTheExchange()
    {
        var signal = OnAnother();

        Assert.Equal(Another, PaperTrade.Open(signal, 100m, PaperTestData.Now, 0).Exchange);

        var received = ReceivedSignal.From(signal, PaperTestData.Now, null);
        Assert.Equal(Another, received.Exchange);
        Assert.Equal(signal, received.ToTradeSignal());
    }

    [Fact]
    public void ABinanceSignalIsStoredAsBinance() =>
        Assert.Equal(ExchangeEnum.Binance, ReceivedSignal.From(PaperTestData.Long(), PaperTestData.Now, null).Exchange);
}
