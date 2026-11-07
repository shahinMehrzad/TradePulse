using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Services;

/// <summary>
/// The receiver's rules for a signal, in the order they are checked; the first that fails is the answer. Nothing here reads the clock, a
/// price or the database: the caller passes them in, so every rule can be tested with plain numbers.
/// A repeated signal (same id) is not decided here, the database's unique key does that.
/// </summary>
public static class SignalValidator
{
    /// <summary>How far ahead of the receiver's clock a candle's close may be, for two machines that disagree by a little.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    /// <param name="livePrice">The latest price the receiver has, which would be the entry.</param>
    /// <param name="openTradesOnSymbol">Open trades of this strategy on the signal's symbol.</param>
    /// <returns><c>null</c> when the signal is taken, otherwise why not.</returns>
    public static SignalRejectionEnum? Validate(
        TradeSignal signal, SignalStrategySettings settings, decimal livePrice, DateTime now, int openTradesOnSymbol)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(livePrice);

        if (!settings.Enabled)
            return SignalRejectionEnum.StrategyDisabled;

        if (signal.Market != MarketTypeEnum.Futures)
            return SignalRejectionEnum.UnsupportedMarket;

        if (signal.SignalId != SignalId.Build(signal.Strategy, signal.Symbol, signal.TimeFrame, signal.CandleOpenTime, signal.Side))
            return SignalRejectionEnum.SignalIdMismatch;

        if (signal.CandleCloseTime != signal.CandleOpenTime + TimeSpan.FromSeconds((int)signal.TimeFrame))
            return SignalRejectionEnum.InconsistentCandleTimes;

        if (signal.Leverage < 1 || signal.Leverage > settings.MaxLeverage)
            return SignalRejectionEnum.LeverageOutOfRange;

        if (!LevelsAreOnTheRightSide(signal))
            return SignalRejectionEnum.LevelsOnWrongSide;

        if (signal.CandleCloseTime > now + ClockSkew)
            return SignalRejectionEnum.SignalFromTheFuture;

        if (now > signal.ValidUntil)
            return SignalRejectionEnum.SignalExpired;

        if (now - signal.CandleCloseTime > settings.MaxSignalAge)
            return SignalRejectionEnum.SignalTooOld;

        if (IsPastALevel(signal, livePrice))
            return SignalRejectionEnum.PriceBeyondLevels;

        if (Math.Abs(livePrice - signal.SignalPrice) > settings.MaxMoveOfStopDistance * Math.Abs(signal.SignalPrice - signal.StopLoss))
            return SignalRejectionEnum.PriceMovedTooFar;

        if (settings.MaxOpenPerSymbol is { } max && openTradesOnSymbol >= max)
            return SignalRejectionEnum.TooManyOpenTrades;

        return null;
    }

    /// <summary>A long needs stop &lt; price &lt; target, a short target &lt; price &lt; stop; and every price above zero.</summary>
    private static bool LevelsAreOnTheRightSide(TradeSignal s) =>
        s.StopLoss > 0 && s.TakeProfit > 0 && s.SignalPrice > 0
        && (s.Side == PositionSideEnum.Long
            ? s.StopLoss < s.SignalPrice && s.SignalPrice < s.TakeProfit
            : s.TakeProfit < s.SignalPrice && s.SignalPrice < s.StopLoss);

    /// <summary>The live price is already at or past the target or the stop: entering would close the trade on the same breath.</summary>
    private static bool IsPastALevel(TradeSignal s, decimal livePrice) =>
        s.Side == PositionSideEnum.Long
            ? livePrice <= s.StopLoss || livePrice >= s.TakeProfit
            : livePrice >= s.StopLoss || livePrice <= s.TakeProfit;
}
