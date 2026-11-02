using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// A sudden move of the price seen live in the 1 second bars built from the exchange's trades. It states what happened (which way,
/// how far, how unusual), never what will happen: afterwards the price goes on the same way only about as often as it turns back.
/// Flagged seconds less than five minutes apart are one alert; it keeps the first second and the largest move.
/// </summary>
public class ShockAlert
{
    public int Id { get; set; }
    public int SymbolListingId { get; set; }
    public SymbolListing? SymbolListing { get; set; }

    /// <summary>UTC. The first flagged second. Stored as <c>timestamp</c> (no zone) like every other time column.</summary>
    public DateTime TriggeredAt { get; set; }

    public EventDirectionEnum Direction { get; set; }

    /// <summary>The highest level the alert reached while it lasted.</summary>
    public AlertLevelEnum Level { get; set; }

    /// <summary>The length of the window the move was measured over, in seconds.</summary>
    public int WindowSeconds { get; set; }

    /// <summary>The largest move inside the alert, as a fraction (0.0062 is +0.62 %); negative when the price fell.</summary>
    public decimal Move { get; set; }

    /// <summary>That move measured in standard deviations of the 1 second returns of the previous hour, scaled to the window.</summary>
    public decimal ZScore { get; set; }

    /// <summary>The price at the start of the window of the largest move, and at its end.</summary>
    public decimal StartPrice { get; set; }
    public decimal EndPrice { get; set; }

    /// <summary>Base asset traded inside that window.</summary>
    public decimal Volume { get; set; }

    /// <summary>How many seconds were flagged.</summary>
    public int FlaggedSeconds { get; set; }

    /// <summary>Which rule and thresholds found it, so alerts of different settings are not mixed up when they are reviewed.</summary>
    public string RuleVersion { get; set; } = string.Empty;

    public DateTime CreateDate { get; set; }
}
