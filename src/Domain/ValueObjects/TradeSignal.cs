using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// A signal as the webhook contract describes it. The strategy behind it is not known here: <paramref name="Strategy"/> is only a label
/// (<c>S1</c>). Prices are absolute. All times are UTC.
/// <para>
/// <paramref name="CandleOpenTime"/> is the open time of the CLOSED candle that produced the signal, so a signal that arrives at
/// 08:00:02 carries 04:00 for a 4h candle; <paramref name="CandleCloseTime"/> is that time plus the time frame (08:00). The signal id
/// is built from the open time (<see cref="Domain.Services.SignalId"/>), and a signal's age is counted from the close time.
/// </para>
/// <para>
/// <paramref name="Exchange"/> is the exchange the symbol is on. A sender that does not say takes Binance, the only one so far; its signal ids
/// do not change when another exchange comes (only the ids of that other exchange carry its name).
/// </para>
/// </summary>
public sealed record TradeSignal(
    string SignalId,
    string Strategy,
    MarketTypeEnum Market,
    string Symbol,
    PositionSideEnum Side,
    decimal Leverage,
    decimal TakeProfit,
    decimal StopLoss,
    decimal SignalPrice,
    TimeFrameEnum TimeFrame,
    DateTime CandleOpenTime,
    DateTime CandleCloseTime,
    DateTime ValidUntil,
    TimeSpan? MaxDuration = null,
    ExchangeEnum Exchange = ExchangeEnum.Binance);
