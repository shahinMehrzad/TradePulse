using Domain.ValueObjects;

namespace Domain.Entities;

/// <summary>
/// What the receiver allows from one strategy of the signals it takes. A strategy is only a label (<c>S1</c>); the program that makes the
/// signals is not known here. A signal from a label that has no row is refused: a new strategy starts by an admin adding it, and a typo in
/// the sender does not trade. A row that is switched off stays, to remember that it was turned off on purpose.
/// <para>
/// The limits are held by the receiver and cannot be loosened by the sender (<see cref="TradeSignal"/> can only ask for something
/// stricter). They live in the database so that an admin changes them without a deploy; the receiver reads them for every signal.
/// </para>
/// </summary>
public class SignalStrategyConfiguration
{
    public const int MaxNameLength = 32;

    /// <summary>The longest age the receiver takes a signal at: after a week nothing the strategy saw is still true.</summary>
    public static readonly TimeSpan LongestAge = TimeSpan.FromDays(7);

    /// <summary>Binance's highest leverage on any symbol; a larger number is a typo.</summary>
    public const decimal HighestLeverage = 125m;

    public int Id { get; set; }

    /// <summary>The label the signals carry. Unique, and it cannot be changed: trades and signals keep it.</summary>
    public string Strategy { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    /// <summary>How long after its candle closed a signal is still taken, in seconds (<see cref="SignalStrategySettings.MaxSignalAge"/>); <c>null</c>: no limit, the sender's own <c>validUntil</c> decides.</summary>
    public int? MaxSignalAgeSeconds { get; set; }

    /// <summary>How far the live price may be from the signal's price, as a part of the distance from that price to the stop (<see cref="SignalStrategySettings.MaxMoveOfStopDistance"/>).</summary>
    public decimal MaxMoveOfStopDistance { get; set; }

    /// <summary>How many trades of this strategy may be open on one symbol; <c>null</c>: no limit.</summary>
    public int? MaxOpenPerSymbol { get; set; }

    /// <summary>
    /// The highest leverage a signal of this strategy may carry. Always set (never <c>null</c>). The leverage in a signal is itself a ceiling, not a
    /// recommendation: the highest one at which the stop still comes before the liquidation (1 when the sender sees no safe leverage).
    /// </summary>
    public decimal MaxLeverage { get; set; }

    /// <summary>UTC. Stored as <c>timestamp</c> (no zone) like every other time column.</summary>
    public DateTime CreateDate { get; set; }

    /// <summary>
    /// A label goes into the signal id, which is split on colons and read by people: letters, digits, <c>_</c> and <c>-</c>, up to
    /// <see cref="MaxNameLength"/> of them. Nothing else, so a name can never be mistaken for a part of the id or for anything else.
    /// </summary>
    public static bool IsValidName(string? name) =>
        name is { Length: >= 1 and <= MaxNameLength } && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>What is wrong with these values, in words fit to show to an admin; <c>null</c> when they are fine.</summary>
    public string? FindProblem()
    {
        if (!IsValidName(Strategy))
            return $"The name of a strategy is 1 to {MaxNameLength} letters, digits, _ or -.";

        if (MaxSignalAgeSeconds is { } age && (age < 1 || age > (int)LongestAge.TotalSeconds))
            return $"The age limit is 1 second to {LongestAge.TotalDays:0} days, or empty for no limit.";

        if (MaxMoveOfStopDistance <= 0 || MaxMoveOfStopDistance > 1)
            return "The price move limit is a part of the stop distance: more than 0 and at most 1.";

        if (MaxOpenPerSymbol is < 1)
            return "The limit of open trades is at least 1, or empty for no limit.";

        if (MaxLeverage < 1 || MaxLeverage > HighestLeverage)
            return $"The leverage limit is 1 to {HighestLeverage:0}.";

        return null;
    }

    public SignalStrategySettings ToSettings() =>
        new(IsEnabled, MaxSignalAgeSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null, MaxMoveOfStopDistance, MaxOpenPerSymbol, MaxLeverage);
}
