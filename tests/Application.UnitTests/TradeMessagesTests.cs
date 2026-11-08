using Application.Signals;
using Domain.Entities;
using Domain.Enums;
using static Application.UnitTests.SignalTestData;

namespace Application.UnitTests;

/// <summary>The exact words of the messages: what the admins read.</summary>
public class TradeMessagesTests
{
    /// <summary>A long at 100 (target 104, stop 98, leverage 5) that went down to 99 and ended past its target at 104.5, 5 h 15 min after it opened.</summary>
    private static PaperTrade Won()
    {
        var trade = Long();
        trade.Observe(99m, Opened.AddHours(1));
        trade.Observe(104.5m, Opened.AddHours(5).AddMinutes(15).AddSeconds(28));
        trade.Settle([new FundingRate(Opened.AddHours(2), 0.0001m, 101m)]);
        return trade;
    }

    [Fact]
    public void TheMessageWhenATradeOpensSaysWhatWasDone()
    {
        var text = TradeMessages.Opened(Long());

        Assert.Equal("""
            🟢 <b>Paper trade opened · BTC/USDT</b>
            Side: <b>Long ×5</b>
            Entry: 100.0
            Target: 104.0 (+4.00%)
            Stop: 98.0 (−2.00%)
            Strategy: S1
            Risk to reward: 1 : 2.0
            Status: ⏳ open since 08:00 UTC
            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void AShortShowsItsTargetBelowAndItsStopAbove()
    {
        var text = TradeMessages.Opened(Short());

        Assert.Contains("Side: <b>Short ×5</b>", text);
        Assert.Contains("Target: 96.0 (−4.00%)", text);
        Assert.Contains("Stop: 102.0 (+2.00%)", text);
    }

    [Fact]
    public void PricesKeepTheirThousandsAndTheirSmallDecimals()
    {
        var trade = PaperTrade.Open(
            new Domain.ValueObjects.TradeSignal(Domain.Services.SignalId.Build("S1", "ADA/USDT", TimeFrameEnum._4H, CandleOpen, PositionSideEnum.Long), "S1",
                MarketTypeEnum.Futures, "ADA/USDT", PositionSideEnum.Long, 2.5m, 0.46215m, 0.4399m, 0.4505m, TimeFrameEnum._4H, CandleOpen, CandleOpen.AddHours(4), CandleOpen.AddHours(5)),
            0.4505m, Opened, 0);

        var text = TradeMessages.Opened(trade);

        Assert.Contains("Entry: 0.4505", text);
        Assert.Contains("Target: 0.46215", text);
        Assert.Contains("Side: <b>Long ×2.5</b>", text);
        Assert.Contains("Risk to reward: 1 : 1.1", text);
    }

    [Fact]
    public void TheLargePriceOfBitcoinHasItsSeparators()
    {
        var trade = PaperTrade.Open(
            new Domain.ValueObjects.TradeSignal(Domain.Services.SignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, CandleOpen, PositionSideEnum.Long), "S1",
                MarketTypeEnum.Futures, "BTC/USDT", PositionSideEnum.Long, 5m, 68900m, 66800m, 67420.5m, TimeFrameEnum._4H, CandleOpen, CandleOpen.AddHours(4), CandleOpen.AddHours(5)),
            67420.5m, Opened, 0);

        var text = TradeMessages.Opened(trade);

        Assert.Contains("Entry: 67,420.5", text);
        Assert.Contains("Target: 68,900.0 (+2.19%)", text);
        Assert.Contains("Stop: 66,800.0 (−0.92%)", text);
    }

    [Fact]
    public void TheMessageOfATradeThatWonShowsTheMoveTheCostsAndTheNet()
    {
        var text = TradeMessages.Ended(Won());

        Assert.Equal("""
            ✅ <b>Target hit · BTC/USDT</b>
            Side: <b>Long ×5</b>
            Entry: 100.0
            Exit: 104.5
            Closed: 2026-11-07 13:15 UTC
            Duration: 5h 15m
            ────────
            Result with leverage: +22.50%
            Exchange fees (entry and exit): −0.41%
            Funding: −0.05%
            Exchange cost (fees + funding): −0.46%
            <b>Net result: +22.04%</b>
            Worst drawdown on the way: −1.00%
            R: +2.3
            Strategy: S1
            """.ReplaceLineEndings("\n"), text);
    }

    [Fact]
    public void ATradeThatLostShowsAStopAndANegativeNet()
    {
        var trade = Long();
        trade.Observe(97.5m, Opened.AddDays(2).AddHours(3));
        trade.Settle([]);

        var text = TradeMessages.Ended(trade);

        Assert.Contains("🛑 <b>Stop hit · BTC/USDT</b>", text);
        Assert.Contains("Duration: 2d 3h 0m", text);
        Assert.Contains("Result with leverage: −12.50%", text);
        Assert.Contains("Funding: 0.00%", text);
        Assert.Contains("<b>Net result: −12.90%</b>", text);   // 12.5 and 0.40 of fees
        Assert.Contains("R: −1.3", text);
    }

    [Fact]
    public void FundingThatWasReceivedIsShownAsGain()
    {
        var trade = Long();
        trade.Observe(104.5m, Opened.AddHours(10));
        trade.Settle([new FundingRate(Opened.AddHours(2), -0.0001m, 100m)]);

        var text = TradeMessages.Ended(trade);

        Assert.Contains("Funding: +0.05%", text);
        Assert.Contains("Exchange cost (fees + funding): −0.36%", text);
    }

    [Fact]
    public void AShortsWorstPriceIsTheHighestOne()
    {
        var trade = Short();
        trade.Observe(101m, Opened.AddHours(1));
        trade.Observe(95m, Opened.AddHours(2));
        trade.Settle([]);

        Assert.Contains("Worst drawdown on the way: −1.00%", TradeMessages.Ended(trade));
    }

    [Fact]
    public void ATradeThatNeverWentAgainstItsHolderShowsNoDrop()
    {
        var trade = Long();
        trade.Observe(104.5m, Opened.AddHours(1));
        trade.Settle([]);

        Assert.Contains("Worst drawdown on the way: 0.00%", TradeMessages.Ended(trade));
    }

    [Fact]
    public void AnExpiredTradeSaysSo()
    {
        var trade = Long(maxDuration: TimeSpan.FromHours(24));
        trade.Observe(101m, Opened.AddHours(24));
        trade.Settle([]);

        var text = TradeMessages.Ended(trade);

        Assert.Contains("⌛ <b>Expired · BTC/USDT</b>", text);
        Assert.Contains("Exit: 101.0", text);
        Assert.Contains("Duration: 1d 0h 0m", text);
    }

    [Fact]
    public void TheReplyIsOneLineThatSaysWhatHappenedAndTheNet()
    {
        Assert.Equal("✅ BTC/USDT Long target hit · net +22.04%", TradeMessages.EndedReply(Won()));
    }

    [Fact]
    public void TheMessageOfATradeWithoutAResultCannotBeMade()
    {
        var trade = Long();
        trade.Observe(104.5m, Opened.AddHours(1));

        Assert.Throws<InvalidOperationException>(() => TradeMessages.Ended(trade));
        Assert.Throws<InvalidOperationException>(() => TradeMessages.EndedReply(trade));
    }
}
