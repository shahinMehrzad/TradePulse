using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Entities;

/// <summary>
/// A virtual position opened from a signal. It never touches an exchange; it only follows prices and says when the target or the stop was
/// reached. It is closed at most once: after that, every price is ignored.
/// <para>
/// The exit is the price that was seen, not the level: when the price jumps past a stop, the trade closes at the jump's price, as a real
/// stop order would fill. Times are UTC and stored as <c>timestamp</c> (no zone) like every other time column.
/// </para>
/// </summary>
public class PaperTrade
{
    public int Id { get; private set; }
    public string SignalId { get; private set; } = string.Empty;
    public string Strategy { get; private set; } = string.Empty;

    /// <summary>Like <c>BTC/USDT</c>.</summary>
    public string Symbol { get; private set; } = string.Empty;
    public PositionSideEnum Side { get; private set; }
    public decimal Leverage { get; private set; }
    public decimal TakeProfit { get; private set; }
    public decimal StopLoss { get; private set; }

    /// <summary>What the strategy saw. Kept next to <see cref="EntryPrice"/> so the slippage of every trade can be studied later.</summary>
    public decimal SignalPrice { get; private set; }

    /// <summary>The first live price after the signal arrived.</summary>
    public decimal EntryPrice { get; private set; }
    public DateTime OpenedAt { get; private set; }

    /// <summary>Open trades of the same strategy on the same symbol when this one opened, so results can be compared with a one-position-at-a-time backtest.</summary>
    public int OpenTradesAtEntry { get; private set; }

    /// <summary><c>null</c>: stays open until the target or the stop.</summary>
    public DateTime? ExpiresAt { get; private set; }

    public PaperTradeStatusEnum Status { get; private set; }
    public decimal? ExitPrice { get; private set; }
    public DateTime? ClosedAt { get; private set; }

    /// <summary>
    /// The best and the worst price the trade saw from its entry until it ended (for a long the highest and the lowest, for a short the other
    /// way round), so the message can say how far the trade went against its holder on the way.
    /// </summary>
    public decimal BestPrice { get; private set; }
    public decimal WorstPrice { get; private set; }

    /// <summary>The message of this trade in the chat, so it can be edited when the trade ends. Both are set together, once.</summary>
    public long? TelegramChatId { get; private set; }
    public int? TelegramMessageId { get; private set; }

    /// <summary>
    /// The result, set by <see cref="Settle"/> when the trade has ended; every part is a fraction of the margin (see <see cref="PaperPnl"/>).
    /// Costs are positive when paid.
    /// </summary>
    public decimal? GrossResult { get; private set; }
    public decimal? FeesPaid { get; private set; }
    public decimal? FundingPaid { get; private set; }
    public decimal? NetResult { get; private set; }
    public decimal? RMultiple { get; private set; }

    public bool IsOpen => Status == PaperTradeStatusEnum.Open;

    /// <summary>Reward over risk at the entry: the distance to the target divided by the distance to the stop.</summary>
    public decimal RiskReward => Math.Abs(TakeProfit - EntryPrice) / Math.Abs(EntryPrice - StopLoss);

    private PaperTrade() { }

    public static PaperTrade Open(TradeSignal signal, decimal entryPrice, DateTime openedAt, int openTradesAtEntry)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(entryPrice);
        ArgumentOutOfRangeException.ThrowIfNegative(openTradesAtEntry);

        var trade = new PaperTrade
        {
            SignalId = signal.SignalId,
            Strategy = signal.Strategy,
            Symbol = signal.Symbol,
            Side = signal.Side,
            Leverage = signal.Leverage,
            TakeProfit = signal.TakeProfit,
            StopLoss = signal.StopLoss,
            SignalPrice = signal.SignalPrice,
            EntryPrice = entryPrice,
            BestPrice = entryPrice,
            WorstPrice = entryPrice,
            OpenedAt = openedAt,
            OpenTradesAtEntry = openTradesAtEntry,
            ExpiresAt = signal.MaxDuration is { } duration ? openedAt + duration : null,
            Status = PaperTradeStatusEnum.Open,
        };

        if (trade.IsPastALevel(entryPrice))
            throw new ArgumentException("The entry price is already at or past the target or the stop.", nameof(entryPrice));

        return trade;
    }

    /// <summary>
    /// A price seen at <paramref name="at"/>. Closes the trade when it reached the target or the stop, or when the time is up.
    /// A price older than the entry is ignored, which makes replaying history safe.
    /// </summary>
    /// <returns>True when this price closed the trade.</returns>
    public bool Observe(decimal price, DateTime at)
    {
        if (!IsOpen || at < OpenedAt)
            return false;

        Track(price);

        if (HitTarget(price))
            return Close(PaperTradeStatusEnum.TakeProfit, price, at);

        if (HitStop(price))
            return Close(PaperTradeStatusEnum.StopLoss, price, at);

        return ExpiresAt is { } expires && at >= expires && Close(PaperTradeStatusEnum.Expired, price, at);
    }

    /// <summary>
    /// A finished candle, for the time the collector was not watching ticks (a restart, a dropped socket). Only candles that opened at or after
    /// the entry count: the one that holds the entry cannot tell what came before it.
    /// <para>
    /// Inside a candle the order of events is unknown. When both the target and the stop lie inside it, the stop wins (the bad outcome is
    /// the one assumed). A candle that opens beyond a level closes the trade at that opening price, like a gap. The close time is the
    /// candle's close: the real moment is somewhere inside it.
    /// </para>
    /// </summary>
    /// <returns>True when this candle closed the trade.</returns>
    public bool ObserveCandle(DateTime openTime, decimal open, decimal high, decimal low, decimal close, DateTime closeTime)
    {
        if (!IsOpen || openTime < OpenedAt)
            return false;

        var isLong = Side == PositionSideEnum.Long;

        if (isLong ? low <= StopLoss : high >= StopLoss)
            return Close(PaperTradeStatusEnum.StopLoss, isLong ? Math.Min(open, StopLoss) : Math.Max(open, StopLoss), closeTime);

        if (isLong ? high >= TakeProfit : low <= TakeProfit)
            return Close(PaperTradeStatusEnum.TakeProfit, isLong ? Math.Max(open, TakeProfit) : Math.Min(open, TakeProfit), closeTime);

        Track(high);
        Track(low);

        return ExpiresAt is { } expires && closeTime >= expires && Close(PaperTradeStatusEnum.Expired, close, closeTime);
    }

    /// <summary>Remembers where the message of this trade is, so the same message can be edited later. A trade has one message.</summary>
    public void AttachMessage(long chatId, int messageId)
    {
        if (TelegramMessageId is not null)
            throw new InvalidOperationException("This trade already has a message.");

        TelegramChatId = chatId;
        TelegramMessageId = messageId;
    }

    /// <summary>Works out the result of the ended trade (see <see cref="PaperPnl.Calculate"/>) and keeps it on the trade.</summary>
    public PaperPnl Settle(IEnumerable<FundingRate> fundingHistory, decimal takerFee = PaperPnl.DefaultTakerFee)
    {
        var pnl = PaperPnl.Calculate(this, fundingHistory, takerFee);

        GrossResult = pnl.Gross;
        FeesPaid = pnl.Fees;
        FundingPaid = pnl.Funding;
        NetResult = pnl.Net;
        RMultiple = pnl.RMultiple;
        return pnl;
    }

    private void Track(decimal price)
    {
        var better = Side == PositionSideEnum.Long ? price > BestPrice : price < BestPrice;
        var worse = Side == PositionSideEnum.Long ? price < WorstPrice : price > WorstPrice;

        if (better)
            BestPrice = price;
        if (worse)
            WorstPrice = price;
    }

    private bool HitTarget(decimal price) => Side == PositionSideEnum.Long ? price >= TakeProfit : price <= TakeProfit;

    private bool HitStop(decimal price) => Side == PositionSideEnum.Long ? price <= StopLoss : price >= StopLoss;

    private bool IsPastALevel(decimal price) => HitTarget(price) || HitStop(price);

    private bool Close(PaperTradeStatusEnum status, decimal price, DateTime at)
    {
        Track(price);
        Status = status;
        ExitPrice = price;
        ClosedAt = at;
        return true;
    }
}
