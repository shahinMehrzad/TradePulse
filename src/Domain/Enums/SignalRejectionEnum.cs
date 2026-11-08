namespace Domain.Enums;

/// <summary>Why a signal is not turned into a paper trade. The webhook answers 422 with the name; it is never retried.</summary>
public enum SignalRejectionEnum : byte
{
    StrategyDisabled = 1,
    UnsupportedMarket = 2,
    SignalIdMismatch = 3,
    InconsistentCandleTimes = 4,
    LeverageOutOfRange = 5,
    LevelsOnWrongSide = 6,
    SignalFromTheFuture = 7,
    SignalExpired = 8,
    SignalTooOld = 9,
    PriceBeyondLevels = 10,
    PriceMovedTooFar = 11,
    TooManyOpenTrades = 12,

    /// <summary>The strategy has no row in the settings: a signal from a label nobody added is not taken.</summary>
    UnknownStrategy = 13,

    /// <summary>The symbol is not listed on the exchange market (the panel's Symbols page), so its price is not followed.</summary>
    UnknownSymbol = 14
}
