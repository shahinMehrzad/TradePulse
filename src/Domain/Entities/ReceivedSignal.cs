using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Entities;

/// <summary>
/// A signal as it was received, with what was decided about it: <see cref="Rejection"/> is <c>null</c> when it was taken. Every signal is
/// kept, the refused ones too, so a sender that asks why nothing happened has an answer. The signal id is unique: a signal sent twice is
/// one row.
/// </summary>
public class ReceivedSignal
{
    public int Id { get; set; }
    public string SignalId { get; set; } = string.Empty;
    public string Strategy { get; set; } = string.Empty;
    public MarketTypeEnum Market { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public PositionSideEnum Side { get; set; }
    public decimal Leverage { get; set; }
    public decimal TakeProfit { get; set; }
    public decimal StopLoss { get; set; }
    public decimal SignalPrice { get; set; }
    public TimeFrameEnum TimeFrame { get; set; }

    /// <summary>UTC. The open time of the closed candle that made the signal. The times are <c>timestamp</c> (no zone) in the database.</summary>
    public DateTime CandleOpenTime { get; set; }
    public DateTime CandleCloseTime { get; set; }
    public DateTime ValidUntil { get; set; }

    /// <summary>How long the trade may stay open, in seconds; <c>null</c>: until the target or the stop.</summary>
    public int? MaxDurationSeconds { get; set; }

    public DateTime ReceivedAt { get; set; }

    /// <summary>Why it was refused; <c>null</c> when it was taken.</summary>
    public SignalRejectionEnum? Rejection { get; set; }

    public bool WasTaken => Rejection is null;

    public static ReceivedSignal From(TradeSignal signal, DateTime receivedAt, SignalRejectionEnum? rejection) => new()
    {
        SignalId = signal.SignalId,
        Strategy = signal.Strategy,
        Market = signal.Market,
        Symbol = signal.Symbol,
        Side = signal.Side,
        Leverage = signal.Leverage,
        TakeProfit = signal.TakeProfit,
        StopLoss = signal.StopLoss,
        SignalPrice = signal.SignalPrice,
        TimeFrame = signal.TimeFrame,
        CandleOpenTime = signal.CandleOpenTime,
        CandleCloseTime = signal.CandleCloseTime,
        ValidUntil = signal.ValidUntil,
        MaxDurationSeconds = signal.MaxDuration is { } duration ? (int)duration.TotalSeconds : null,
        ReceivedAt = receivedAt,
        Rejection = rejection,
    };

    public TradeSignal ToTradeSignal() => new(
        SignalId, Strategy, Market, Symbol, Side, Leverage, TakeProfit, StopLoss, SignalPrice, TimeFrame,
        DateTime.SpecifyKind(CandleOpenTime, DateTimeKind.Utc), DateTime.SpecifyKind(CandleCloseTime, DateTimeKind.Utc),
        DateTime.SpecifyKind(ValidUntil, DateTimeKind.Utc), MaxDurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
}
