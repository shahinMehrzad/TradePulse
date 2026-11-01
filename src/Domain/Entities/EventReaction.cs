namespace Domain.Entities;

/// <summary>What the price did in the time after a <see cref="MarketEvent"/> began. One row per horizon (1, 5, 30, 60 minutes, ...).</summary>
public class EventReaction
{
    public int Id { get; set; }
    public int MarketEventId { get; set; }
    public MarketEvent? MarketEvent { get; set; }

    public int HorizonMinutes { get; set; }

    /// <summary>Close at the end of the horizon against the close of the first flagged minute, as a fraction.</summary>
    public decimal Return { get; set; }

    /// <summary>The highest and lowest price inside the horizon against that close, as fractions. They show how far a stop would have been hit.</summary>
    public decimal MaxRise { get; set; }
    public decimal MaxFall { get; set; }
}
