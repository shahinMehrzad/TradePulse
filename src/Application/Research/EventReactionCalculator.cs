using Domain.Entities;

namespace Application.Research;

/// <summary>What the price did in the minutes after an event began, measured from the close of its first flagged minute.</summary>
public sealed class EventReactionCalculator(MarketEventDetectorOptions options)
{
    /// <param name="bars">1 minute candles, oldest first, reaching past the event by the longest horizon.</param>
    /// <returns>One reaction per horizon the candles cover; a horizon that runs past the data (or over a hole in it) is left out.</returns>
    public IReadOnlyList<EventReaction> Calculate(MarketEvent marketEvent, IReadOnlyList<MinuteBar> bars)
    {
        var reactions = new List<EventReaction>();

        var start = IndexOf(bars, marketEvent.StartedAt);
        if (start < 0)
            return reactions;

        var basePrice = bars[start].Close;
        if (basePrice <= 0)
            return reactions;

        var high = decimal.MinValue;
        var low = decimal.MaxValue;
        var step = 0;

        foreach (var horizon in options.Horizons.Order())
        {
            // Walk on to the end of this horizon, so the longer ones continue where the shorter ones stopped.
            for (; step < horizon; step++)
            {
                var next = start + step + 1;
                if (next >= bars.Count || bars[next].OpenTime != marketEvent.StartedAt.AddMinutes(step + 1))
                    return reactions;

                high = Math.Max(high, bars[next].High);
                low = Math.Min(low, bars[next].Low);
            }

            reactions.Add(new EventReaction
            {
                HorizonMinutes = horizon,
                Return = Math.Round(bars[start + horizon].Close / basePrice - 1, 8),
                MaxRise = Math.Round(high / basePrice - 1, 8),
                MaxFall = Math.Round(low / basePrice - 1, 8),
            });
        }

        return reactions;
    }

    private static int IndexOf(IReadOnlyList<MinuteBar> bars, DateTime openTime)
    {
        int low = 0, high = bars.Count - 1;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var compared = bars[middle].OpenTime.CompareTo(openTime);
            if (compared == 0)
                return middle;

            if (compared < 0)
                low = middle + 1;
            else
                high = middle - 1;
        }

        return -1;
    }
}
