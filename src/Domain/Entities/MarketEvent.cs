using Domain.Enums;

namespace Domain.Entities;

/// <summary>
/// A moment the market moved far more than it usually does, found by the detector in the stored candles (not typed in by hand).
/// Flagged minutes less than an hour apart are one event. Which news caused it is attached later; the event stands on its own.
/// </summary>
public class MarketEvent
{
    public int Id { get; set; }
    public int SymbolListingId { get; set; }
    public SymbolListing? SymbolListing { get; set; }

    /// <summary>UTC. The first flagged minute. Stored as <c>timestamp</c> (no zone) like every other time column.</summary>
    public DateTime StartedAt { get; set; }

    public EventDirectionEnum Direction { get; set; }

    /// <summary>The largest 5 minute move inside the event, as a fraction (0.0501 is +5.01 %); negative when the price fell.</summary>
    public decimal FiveMinuteReturn { get; set; }

    /// <summary>That move measured in standard deviations of the 1 minute returns of the 24 hours before it.</summary>
    public decimal ZScore { get; set; }

    /// <summary>The highest volume of the event's minutes divided by the average volume of the 24 hours before it.</summary>
    public decimal VolumeRatio { get; set; }

    /// <summary>How many minutes were flagged.</summary>
    public int FlaggedMinutes { get; set; }

    /// <summary>Which version of the detector and its thresholds found it, so events of different rules are not mixed up.</summary>
    public string DetectorVersion { get; set; } = string.Empty;

    public DateTime CreateDate { get; set; }

    public List<EventReaction> Reactions { get; set; } = [];
}
