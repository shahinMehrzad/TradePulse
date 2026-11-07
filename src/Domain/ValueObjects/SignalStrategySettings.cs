namespace Domain.ValueObjects;

/// <summary>
/// What the receiver allows from one strategy. The receiver holds these, not the sender: a sender can ask for something stricter
/// (<see cref="TradeSignal.ValidUntil"/>) but never looser.
/// </summary>
/// <param name="MaxSignalAge">How long after its candle closed a signal is still taken (S1 holds trades for days, a faster strategy wants seconds).</param>
/// <param name="MaxMoveOfStopDistance">
/// How far the live price may be from the signal's price, as a part of the distance from that price to the stop (0.25 = a quarter).
/// A fixed percent would not do: the levels are absolute, so any move changes the risk to reward, and a move bigger than the stop distance
/// puts the entry past the stop.
/// </param>
/// <param name="MaxOpenPerSymbol">How many trades of this strategy may be open on one symbol; <c>null</c> = no limit (overlap is allowed on purpose, it is a test).</param>
/// <param name="MaxLeverage">A guard against a typo in the sender, not a trading rule.</param>
public sealed record SignalStrategySettings(
    bool Enabled,
    TimeSpan MaxSignalAge,
    decimal MaxMoveOfStopDistance,
    int? MaxOpenPerSymbol,
    decimal MaxLeverage)
{
    public static SignalStrategySettings Default { get; } = new(
        Enabled: true,
        MaxSignalAge: TimeSpan.FromMinutes(60),
        MaxMoveOfStopDistance: 0.25m,
        MaxOpenPerSymbol: null,
        MaxLeverage: 20m);
}
