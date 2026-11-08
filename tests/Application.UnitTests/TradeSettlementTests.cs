using Application.Signals;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Application.UnitTests.SignalTestData;

namespace Application.UnitTests;

/// <summary>What an ended trade earned: the move, the fees, and the funding asked of the exchange for its own stretch.</summary>
public class TradeSettlementTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // The trade opens at 08:00:02 and ends at the target eleven days later, at 12:30:00: well away from a funding moment.
    private static readonly DateTime EndedAt = FirstMinute.AddDays(11).AddHours(4).AddMinutes(30);

    private readonly FakeFundingGateway _funding = new();
    private readonly FakeSignalStore _store = new();
    private readonly FakeFollowedSymbols _symbols = new(Btc);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Opened));   // the tests that care about the moment set it forward

    private TradeSettlement NewSettlement(SettlementOptions? options = null) =>
        new(_funding, _store, _symbols, options ?? new SettlementOptions(), _time, NullLogger<TradeSettlement>.Instance);

    /// <summary>A long at 100, target 102 and stop 98, leverage 5 (the same numbers as the result tests of the domain), ended at the target.</summary>
    private static PaperTrade EndedLong(DateTime? at = null)
    {
        var trade = LongWithTarget102();
        trade.Observe(102m, at ?? EndedAt);
        return trade;
    }

    private static PaperTrade LongWithTarget102()
    {
        var open = CandleOpen;
        var signal = new Domain.ValueObjects.TradeSignal(
            Domain.Services.SignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, open, PositionSideEnum.Long), "S1", MarketTypeEnum.Futures, "BTC/USDT",
            PositionSideEnum.Long, 5m, 102m, 98m, 100m, TimeFrameEnum._4H, open, open.AddHours(4), open.AddHours(4).AddMinutes(45));
        return PaperTrade.Open(signal, 100m, Opened, 0);
    }

    private static FundingRate Rate(DateTime at, decimal rate, decimal mark = 100m) => new(at, rate, mark);

    [Fact]
    public async Task TheResultIsTheMoveMinusTheFeesMinusTheFunding()
    {
        var trade = EndedLong();
        _funding.Add(Rate(FirstMinute.AddHours(8), 0.0001m, 101m));   // one settlement while the trade was open

        var outcome = await NewSettlement().SettleAsync(trade, Ct);

        var settled = Assert.IsType<SettlementOutcome.Settled>(outcome);
        Assert.Equal(0.10m, settled.Result.Gross);
        Assert.Equal(0.00404m, settled.Result.Fees);
        Assert.Equal(0.000505m, settled.Result.Funding);
        Assert.Equal(0.10m - 0.00404m - 0.000505m, trade.NetResult);
        Assert.Same(trade, Assert.Single(_store.Settled));
    }

    [Fact]
    public async Task EverySettlementWhileTheTradeWasOpenIsCounted()
    {
        var trade = EndedLong();
        var first = FirstMinute.AddHours(8);   // 16:00 on the first day
        _funding.Add(Enumerable.Range(0, 36).Select(i => Rate(first.AddHours(8 * i), 0.0001m)).ToArray());   // eleven days and then some

        var outcome = await NewSettlement().SettleAsync(trade, Ct);

        // Eleven days ending at 12:30: 33 settlements; the three after it ended are not counted.
        Assert.Equal(33 * 0.0005m, Assert.IsType<SettlementOutcome.Settled>(outcome).Result.Funding);
    }

    [Fact]
    public async Task ANegativeRateIsReceivedByALong()
    {
        var trade = EndedLong();
        _funding.Add(Rate(FirstMinute.AddHours(8), -0.0001m, 100m));

        var outcome = await NewSettlement().SettleAsync(trade, Ct);

        Assert.Equal(-0.0005m, Assert.IsType<SettlementOutcome.Settled>(outcome).Result.Funding);
    }

    [Fact]
    public async Task ASettlementBeforeTheEntryOrAfterTheEndIsNotCounted()
    {
        var trade = EndedLong();
        _funding.Add(Rate(FirstMinute.AddHours(-8), 0.01m), Rate(EndedAt.AddHours(10), 0.01m));

        var outcome = await NewSettlement().SettleAsync(trade, Ct);

        Assert.Equal(0m, Assert.IsType<SettlementOutcome.Settled>(outcome).Result.Funding);
    }

    [Fact]
    public async Task ItAsksForTheFundingFromTheEntryAndByTheNameTheExchangeUses()
    {
        var trade = EndedLong();

        await NewSettlement().SettleAsync(trade, Ct);

        var call = Assert.Single(_funding.Calls);
        Assert.Equal((ExchangeEnum.Binance, "BTCUSDT"), (call.Exchange, call.Symbol));
        Assert.Equal(Opened, call.From);
    }

    [Fact]
    public async Task ASymbolThatIsNotListedAnyMoreIsAskedForByItsNameWithoutTheSlash()
    {
        _symbols.Symbols.Clear();

        await NewSettlement().SettleAsync(EndedLong(), Ct);

        Assert.Equal("BTCUSDT", Assert.Single(_funding.Calls).Symbol);
    }

    [Fact]
    public async Task AListThatCannotBeReadDoesNotStopTheSettlement()
    {
        _symbols.Throws = true;

        var outcome = await NewSettlement().SettleAsync(EndedLong(), Ct);

        Assert.IsType<SettlementOutcome.Settled>(outcome);
    }

    [Fact]
    public async Task SeveralPagesAreReadUntilTheEndOfTheTrade()
    {
        var trade = EndedLong();
        _funding.PageSize = 10;
        var first = FirstMinute.AddHours(8);
        _funding.Add(Enumerable.Range(0, 40).Select(i => Rate(first.AddHours(8 * i), 0.0001m)).ToArray());

        var outcome = await NewSettlement().SettleAsync(trade, Ct);

        Assert.Equal(33 * 0.0005m, Assert.IsType<SettlementOutcome.Settled>(outcome).Result.Funding);
        Assert.True(_funding.Calls.Count >= 4);
    }

    [Fact]
    public async Task WhenTheExchangeDoesNotAnswerNothingIsChangedAndItCanBeSettledLater()
    {
        var trade = EndedLong();
        _funding.Fail = true;

        var failed = await NewSettlement().SettleAsync(trade, Ct);

        Assert.Equal("the exchange is down", Assert.IsType<SettlementOutcome.Failed>(failed).Error);
        Assert.Null(trade.NetResult);
        Assert.Empty(_store.Settled);

        _funding.Fail = false;
        Assert.IsType<SettlementOutcome.Settled>(await NewSettlement().SettleAsync(trade, Ct));
        Assert.NotNull(trade.NetResult);
    }

    [Fact]
    public async Task ATradeThatHasItsResultIsLeftAlone()
    {
        var trade = EndedLong();
        await NewSettlement().SettleAsync(trade, Ct);
        _funding.Fail = true;

        var outcome = await NewSettlement().SettleAsync(trade, Ct);

        Assert.IsType<SettlementOutcome.AlreadySettled>(outcome);
        Assert.Single(_funding.Calls);
    }

    [Fact]
    public async Task ATradeThatIsStillOpenCannotBeSettled() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => NewSettlement().SettleAsync(Long(), Ct));

    [Fact]
    public async Task ATradeThatEndedRightAfterAFundingMomentWaitsForTheSettlementToBePublished()
    {
        var endedAt = FirstMinute.AddDays(1).AddHours(8).AddSeconds(30);   // 16:00:30 the next day
        var trade = EndedLong(endedAt);
        _time.SetUtcNow(new DateTimeOffset(endedAt.AddSeconds(5)));

        var outcome = await NewSettlement().SettleAsync(trade, Ct);

        Assert.Equal(FirstMinute.AddDays(1).AddHours(8).AddMinutes(2), Assert.IsType<SettlementOutcome.Postponed>(outcome).Until);
        Assert.Null(trade.NetResult);
        Assert.Empty(_funding.Calls);
    }

    [Fact]
    public async Task OnceTheLagHasPassedTheSameTradeIsSettledAndThatSettlementIsCounted()
    {
        var endedAt = FirstMinute.AddDays(1).AddHours(8).AddSeconds(30);   // 16:00:30
        var trade = EndedLong(endedAt);
        _funding.Add(Rate(FirstMinute.AddDays(1).AddHours(8), 0.0001m, 100m));   // the 16:00 settlement, paid before it ended
        _time.SetUtcNow(new DateTimeOffset(endedAt.AddMinutes(2)));

        var outcome = await NewSettlement().SettleAsync(trade, Ct);

        Assert.Equal(0.0005m, Assert.IsType<SettlementOutcome.Settled>(outcome).Result.Funding);
    }

    [Fact]
    public async Task ATradeThatEndedAtTheEndOfAnHourDoesNotWaitAtAll()
    {
        var endedAt = FirstMinute.AddDays(1).AddHours(7).AddMinutes(59);   // 15:59
        var trade = EndedLong(endedAt);
        _time.SetUtcNow(new DateTimeOffset(endedAt.AddSeconds(5)));

        Assert.IsType<SettlementOutcome.Settled>(await NewSettlement().SettleAsync(trade, Ct));
    }

    [Fact]
    public async Task TheFeeCanBeChanged()
    {
        var trade = EndedLong();

        var outcome = await NewSettlement(new SettlementOptions { TakerFee = 0.0002m }).SettleAsync(trade, Ct);

        Assert.Equal(0.00202m, Assert.IsType<SettlementOutcome.Settled>(outcome).Result.Fees);
    }
}
