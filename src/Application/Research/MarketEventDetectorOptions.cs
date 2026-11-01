using System.Globalization;

namespace Application.Research;

/// <summary>
/// The rule that decides what counts as a market event: the price moved at least <see cref="MinFiveMinuteMove"/> within 5 minutes and
/// at least <see cref="MinZScore"/> times what it usually moves, measured on the <see cref="Window"/> minutes before.
/// </summary>
public sealed record MarketEventDetectorOptions
{
    /// <summary>How many 1 minute candles before a minute describe what is usual (1440 is 24 hours).</summary>
    public int Window { get; init; } = 1440;

    /// <summary>The smallest 5 minute move, as a logarithmic return, that can be an event (0.01 is about 1 %).</summary>
    public double MinFiveMinuteMove { get; init; } = 0.01;

    public double MinZScore { get; init; } = 5;

    /// <summary>Flagged minutes this close together are one event.</summary>
    public TimeSpan MergeGap { get; init; } = TimeSpan.FromMinutes(60);

    /// <summary>How long after an event the price is followed, in minutes.</summary>
    public IReadOnlyList<int> Horizons { get; init; } = [1, 5, 30, 60];

    /// <summary>Names the rule, so events found by different rules are never taken for each other. Change it when the logic changes.</summary>
    public string Version => string.Create(CultureInfo.InvariantCulture, $"m5-{MinFiveMinuteMove}-z{MinZScore}-w{Window}-v1");
}
