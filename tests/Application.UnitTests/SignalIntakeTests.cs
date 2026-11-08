using Application.Signals;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Application.UnitTests.SignalTestData;

namespace Application.UnitTests;

/// <summary>The whole decision about a signal: who sent it, what it says, whether it is taken, and what is done when it is.</summary>
public class SignalIntakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Secret = "current-secret-of-the-sender";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Opened));   // 08:00:02, two seconds after the 4h candle closed
    private readonly FakeSignalStore _store = new();
    private readonly FakeStrategies _strategies = new();
    private readonly FakeFollowedSymbols _symbols = new(Btc);
    private readonly PriceBoard _board = new();
    private readonly RecordingHandover _handover = new();
    private SignalIntakeOptions _options = new() { Secrets = [Secret] };

    public SignalIntakeTests()
    {
        _strategies.Rows.Add(new SignalStrategyConfiguration
        {
            Id = 1, Strategy = "S1", IsEnabled = true, MaxSignalAgeSeconds = 3600, MaxMoveOfStopDistance = 0.25m, MaxOpenPerSymbol = null, MaxLeverage = 20m,
        });
        _board.Set(ExchangeEnum.Binance, "BTC/USDT", 100m, Opened.AddSeconds(-1));
    }

    private SignalIntake NewIntake() => new(
        new SignalAuthenticator(_options, _time), _store, _strategies, _symbols, _board, _handover, _handover, _options, _time, NullLogger<SignalIntake>.Instance);

    private static string Stamp(DateTime at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");

    /// <summary>Sends a signal the way the sender does: signed, with its id as the key.</summary>
    private Task<IntakeResult> SendAsync(TradeSignal signal, Action<Dictionary<string, object?>>? change = null) => SendBodyAsync(SignalParserTests.Json(signal, change), signal.SignalId);

    private Task<IntakeResult> SendBodyAsync(string body, string? key, string secret = Secret, string? stamp = null)
    {
        stamp ??= Stamp(_time.GetUtcNow().UtcDateTime);
        return NewIntake().TakeAsync(body, stamp, SignalAuthenticator.Sign(secret, stamp, body), key, Ct);
    }

    // ---- who ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARequestSignedWithAnotherSecretIsUnauthorizedAndNothingIsRead()
    {
        var signal = Signal();

        var result = await SendBodyAsync(SignalParserTests.Json(signal), signal.SignalId, secret: "not-the-secret");

        Assert.IsType<IntakeResult.Unauthorized>(result);
        Assert.Empty(_store.Signals);
        Assert.Empty(_handover.Tracked);
    }

    [Fact]
    public async Task ABodyThatIsNotEvenJsonIsUnauthorizedNotMalformedWhenTheSignatureIsWrong()
    {
        var result = await SendBodyAsync("not json at all", "x", secret: "not-the-secret");

        Assert.IsType<IntakeResult.Unauthorized>(result);
    }

    [Fact]
    public async Task ATooLargeBodyIsUnauthorizedWhenTheSignatureIsWrongAndMalformedWhenItIsRight()
    {
        _options = _options with { MaxBodyBytes = 100 };
        var body = SignalParserTests.Json(Signal());

        Assert.IsType<IntakeResult.Unauthorized>(await SendBodyAsync(body, "x", secret: "wrong"));
        Assert.Equal("The body is too large.", Assert.IsType<IntakeResult.Malformed>(await SendBodyAsync(body, "x")).Error);
    }

    [Fact]
    public async Task ARequestWithoutASignatureIsUnauthorized()
    {
        var signal = Signal();

        var result = await NewIntake().TakeAsync(SignalParserTests.Json(signal), Stamp(Opened), null, signal.SignalId, Ct);

        Assert.IsType<IntakeResult.Unauthorized>(result);
    }

    [Fact]
    public async Task ARequestWhoseTimeIsTooFarFromOursIsUnauthorized()
    {
        var signal = Signal();

        var result = await SendBodyAsync(SignalParserTests.Json(signal), signal.SignalId, stamp: Stamp(Opened.AddMinutes(-6)));

        Assert.IsType<IntakeResult.Unauthorized>(result);
    }

    // ---- what it says -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ABodyThatIsNotTheContractIsMalformedAndNothingIsStored()
    {
        var signal = Signal();

        var result = await SendAsync(signal, f => f.Remove("stopLoss"));

        Assert.Contains("stopLoss", Assert.IsType<IntakeResult.Malformed>(result).Error);
        Assert.Empty(_store.Signals);
    }

    [Fact]
    public async Task AKeyThatIsNotTheSignalIdIsMalformed()
    {
        var signal = Signal();

        var result = await SendBodyAsync(SignalParserTests.Json(signal), "another-key");

        Assert.Equal("Idempotency-Key must be the signalId.", Assert.IsType<IntakeResult.Malformed>(result).Error);
        Assert.Empty(_store.Signals);
    }

    [Fact]
    public async Task AMissingKeyIsMalformed()
    {
        var result = await SendBodyAsync(SignalParserTests.Json(Signal()), null);

        Assert.IsType<IntakeResult.Malformed>(result);
    }

    // ---- taken --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASignalThatIsWorthTakingOpensATradeAtTheLatestPrice()
    {
        _board.Set(ExchangeEnum.Binance, "BTC/USDT", 100.2m, Opened.AddSeconds(-1));
        var signal = Signal();

        var result = await SendAsync(signal);

        var accepted = Assert.IsType<IntakeResult.Accepted>(result);
        Assert.Equal(signal.SignalId, accepted.SignalId);
        var trade = Assert.Single(_store.RecordedTrades);
        Assert.Equal(accepted.TradeId, trade.Id);
        Assert.Equal(100.2m, trade.EntryPrice);
        Assert.Equal(100m, trade.SignalPrice);
        Assert.Equal(Opened, trade.OpenedAt);
        Assert.Equal(PositionSideEnum.Long, trade.Side);
        Assert.Equal(0, trade.OpenTradesAtEntry);
    }

    [Fact]
    public async Task ATakenSignalIsStoredWithoutARejectionAndItsTradeIsHandedToTheWatchAndTheChat()
    {
        var signal = Signal();

        await SendAsync(signal);

        var stored = _store.Signals[signal.SignalId];
        Assert.True(stored.WasTaken);
        Assert.Equal(signal, stored.ToTradeSignal());
        Assert.Same(Assert.Single(_store.RecordedTrades), Assert.Single(_handover.Tracked));
        Assert.Same(Assert.Single(_store.RecordedTrades), Assert.Single(_handover.Told));
        Assert.Equal(SignalEventKindEnum.Opened, Assert.Single(_store.Events).Kind);
    }

    [Fact]
    public async Task AShortIsTakenToo()
    {
        var signal = Signal(PositionSideEnum.Short);

        Assert.IsType<IntakeResult.Accepted>(await SendAsync(signal));
        Assert.Equal(PositionSideEnum.Short, Assert.Single(_store.RecordedTrades).Side);
    }

    [Fact]
    public async Task ASignalWithAMaximumDurationOpensATradeThatExpires()
    {
        await SendAsync(Signal(maxDuration: TimeSpan.FromHours(48)));

        Assert.Equal(Opened.AddHours(48), Assert.Single(_store.RecordedTrades).ExpiresAt);
    }

    [Fact]
    public async Task TheTradesAlreadyOpenOfTheStrategyOnTheSymbolAreCountedOnTheNewOne()
    {
        _store.Open.Add(Long(id: 7));
        _store.Open.Add(Long(id: 8, symbol: "ETH/USDT"));   // another symbol: not counted

        await SendAsync(Signal());

        Assert.Equal(1, Assert.Single(_store.RecordedTrades).OpenTradesAtEntry);
    }

    // ---- sent again -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ASignalSentAgainIsTheSameSignalAndNothingIsDoneTwice()
    {
        var signal = Signal();
        await SendAsync(signal);

        var again = await SendAsync(signal);

        Assert.Equal(signal.SignalId, Assert.IsType<IntakeResult.Duplicate>(again).SignalId);
        Assert.Single(_store.RecordedTrades);
        Assert.Single(_handover.Tracked);
        Assert.Single(_handover.Told);
    }

    [Fact]
    public async Task ASignalSentAgainIsNotJudgedAgainEvenIfThePriceIsNowFine()
    {
        var signal = Signal();
        _board.Set(ExchangeEnum.Binance, "BTC/USDT", 103m, Opened);          // too far from 100: a quarter of the 2 stop distance is 0.5
        Assert.Equal(SignalRejectionEnum.PriceMovedTooFar, Assert.IsType<IntakeResult.Rejected>(await SendAsync(signal)).Reason);

        _board.Set(ExchangeEnum.Binance, "BTC/USDT", 100m, Opened);
        var again = await SendAsync(signal);

        Assert.Equal(SignalRejectionEnum.PriceMovedTooFar, Assert.IsType<IntakeResult.Rejected>(again).Reason);
        Assert.Empty(_store.RecordedTrades);
    }

    [Fact]
    public async Task TwoRequestsOfOneSignalAtTheSameMomentOpenOneTrade()
    {
        var signal = Signal();
        await SendAsync(signal);
        _store.HideSignalOnce();   // the second request looked before the first had stored it

        var second = await SendAsync(signal);

        Assert.IsType<IntakeResult.Duplicate>(second);
        Assert.Single(_store.RecordedTrades);
        Assert.Single(_handover.Tracked);
    }

    // ---- refused -------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARefusedSignalIsStoredWithItsReasonAndOpensNothing()
    {
        _board.Set(ExchangeEnum.Binance, "BTC/USDT", 99.2m, Opened);   // 0.8 below: more than a quarter of the stop distance
        var signal = Signal();

        var result = await SendAsync(signal);

        var refused = Assert.IsType<IntakeResult.Rejected>(result);
        Assert.Equal(SignalRejectionEnum.PriceMovedTooFar, refused.Reason);
        Assert.Equal(SignalRejectionEnum.PriceMovedTooFar, _store.Signals[signal.SignalId].Rejection);
        Assert.Empty(_store.RecordedTrades);
        Assert.Empty(_handover.Tracked);
        Assert.Empty(_handover.Told);
        var logged = Assert.Single(_store.Events);
        Assert.Equal((SignalEventKindEnum.Rejected, "PriceMovedTooFar"), (logged.Kind, logged.Detail));
    }

    [Fact]
    public async Task AStrategyWithNoRowIsRefused()
    {
        var signal = Signal(strategy: "S9");

        var result = await SendAsync(signal);

        Assert.Equal(SignalRejectionEnum.UnknownStrategy, Assert.IsType<IntakeResult.Rejected>(result).Reason);
        Assert.Equal(SignalRejectionEnum.UnknownStrategy, _store.Signals[signal.SignalId].Rejection);
    }

    [Fact]
    public async Task AStrategyThatIsSwitchedOffIsRefused()
    {
        _strategies.Rows[0].IsEnabled = false;

        var result = await SendAsync(Signal());

        Assert.Equal(SignalRejectionEnum.StrategyDisabled, Assert.IsType<IntakeResult.Rejected>(result).Reason);
    }

    [Fact]
    public async Task ASymbolThatIsNotListedIsRefusedWhateverItsPrice()
    {
        var signal = Signal(symbol: "XRP/USDT");

        var result = await SendAsync(signal);

        Assert.Equal(SignalRejectionEnum.UnknownSymbol, Assert.IsType<IntakeResult.Rejected>(result).Reason);
        Assert.Empty(_store.RecordedTrades);
    }

    [Fact]
    public async Task ASymbolListedLaterIsTakenFromThatMoment()
    {
        var eth = Signal(symbol: "ETH/USDT");
        Assert.IsType<IntakeResult.Rejected>(await SendAsync(eth));

        _symbols.Symbols.Add(Eth);
        _board.Set(ExchangeEnum.Binance, "ETH/USDT", 100m, Opened);
        var next = Signal(symbol: "ETH/USDT");
        next = next with { SignalId = SignalIdFor(next, candleOpen: CandleOpen.AddHours(4)), CandleOpenTime = CandleOpen.AddHours(4), CandleCloseTime = CandleOpen.AddHours(8), ValidUntil = CandleOpen.AddHours(8).AddMinutes(45) };
        _time.Advance(TimeSpan.FromHours(4));
        _board.Set(ExchangeEnum.Binance, "ETH/USDT", 100m, _time.GetUtcNow().UtcDateTime);

        Assert.IsType<IntakeResult.Accepted>(await SendAsync(next));
    }

    private static string SignalIdFor(TradeSignal s, DateTime candleOpen) =>
        Domain.Services.SignalId.Build(s.Strategy, s.Symbol, s.TimeFrame, candleOpen, s.Side, s.Exchange);

    [Fact]
    public async Task ASignalThatIsTooOldIsRefused()
    {
        _time.Advance(TimeSpan.FromHours(2));
        _board.Set(ExchangeEnum.Binance, "BTC/USDT", 100m, _time.GetUtcNow().UtcDateTime);
        var signal = Signal() with { ValidUntil = CandleOpen.AddHours(9) };

        var result = await SendAsync(signal);

        Assert.Equal(SignalRejectionEnum.SignalTooOld, Assert.IsType<IntakeResult.Rejected>(result).Reason);
    }

    [Fact]
    public async Task ASenderThatSaysTheSignalIsNoLongerValidIsHeard()
    {
        _time.Advance(TimeSpan.FromMinutes(10));
        _board.Set(ExchangeEnum.Binance, "BTC/USDT", 100m, _time.GetUtcNow().UtcDateTime);
        var signal = Signal() with { ValidUntil = CandleOpen.AddHours(4).AddMinutes(5) };

        var result = await SendAsync(signal);

        Assert.Equal(SignalRejectionEnum.SignalExpired, Assert.IsType<IntakeResult.Rejected>(result).Reason);
    }

    [Fact]
    public async Task TheLimitOfOpenTradesIsEnforced()
    {
        _strategies.Rows[0].MaxOpenPerSymbol = 1;
        _store.Open.Add(Long(id: 7));

        var result = await SendAsync(Signal());

        Assert.Equal(SignalRejectionEnum.TooManyOpenTrades, Assert.IsType<IntakeResult.Rejected>(result).Reason);
    }

    [Fact]
    public async Task AnIdThatIsNotBuiltTheWayTheContractSaysIsRefused()
    {
        var signal = Signal() with { SignalId = "S1:BTCUSDT:4h:2026-11-07T08:00Z:long" };   // the close time instead of the open time

        var result = await SendAsync(signal);

        Assert.Equal(SignalRejectionEnum.SignalIdMismatch, Assert.IsType<IntakeResult.Rejected>(result).Reason);
    }

    // ---- not judged -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutARecentPriceTheSignalIsNeitherTakenNorRefusedAndNothingIsStored()
    {
        _board.Set(ExchangeEnum.Binance, "BTC/USDT", 100m, Opened.AddSeconds(-30));   // older than the ten seconds allowed
        var signal = Signal();

        var result = await SendAsync(signal);

        Assert.Equal(signal.SignalId, Assert.IsType<IntakeResult.NoPrice>(result).SignalId);
        Assert.Empty(_store.Signals);
    }

    [Fact]
    public async Task OnceThePriceIsThereTheSameSignalIsTaken()
    {
        _board.Set(ExchangeEnum.Binance, "BTC/USDT", 100m, Opened.AddSeconds(-30));
        var signal = Signal();
        Assert.IsType<IntakeResult.NoPrice>(await SendAsync(signal));

        _board.Set(ExchangeEnum.Binance, "BTC/USDT", 100m, Opened);

        Assert.IsType<IntakeResult.Accepted>(await SendAsync(signal));
    }

    [Fact]
    public async Task ADatabaseThatIsDownIsUnavailableAndNothingIsHandedOver()
    {
        _store.RecordFails = true;

        var result = await SendAsync(Signal());

        Assert.IsType<IntakeResult.Unavailable>(result);
        Assert.Empty(_handover.Tracked);
        Assert.Empty(_handover.Told);
    }

    [Fact]
    public async Task ASignalThatCouldNotBeStoredIsTakenWhenItIsSentAgain()
    {
        var signal = Signal();
        _store.RecordFails = true;
        Assert.IsType<IntakeResult.Unavailable>(await SendAsync(signal));

        _store.RecordFails = false;

        Assert.IsType<IntakeResult.Accepted>(await SendAsync(signal));
        Assert.Single(_store.RecordedTrades);
    }

    [Fact]
    public async Task ALogThatCannotBeWrittenDoesNotStopTheSignalFromBeingTaken()
    {
        _store.EventsFail = true;

        Assert.IsType<IntakeResult.Accepted>(await SendAsync(Signal()));
        Assert.Single(_handover.Tracked);
    }
}
