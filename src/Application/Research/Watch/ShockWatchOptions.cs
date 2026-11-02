using System.Globalization;

namespace Application.Research.Watch;

/// <summary>
/// The rule that decides what counts as a shock: over the last <see cref="WindowSeconds"/> the price moved at least
/// <see cref="AttentionMove"/> and at least <see cref="MinZScore"/> times what it usually moves in that time, measured on the
/// <see cref="BaselineSeconds"/> before. A move of <see cref="AlarmMove"/> or more is an alarm instead of an attention.
/// </summary>
public sealed record ShockWatchOptions
{
    public int WindowSeconds { get; init; } = 30;

    /// <summary>The smallest move, as a logarithmic return, that can be a shock (0.003 is about 0.3 %).</summary>
    public double AttentionMove { get; init; } = 0.003;

    public double AlarmMove { get; init; } = 0.005;

    public double MinZScore { get; init; } = 6;

    /// <summary>How many seconds before describe what is usual (3600 is one hour). The watch is silent until it has seen that many.</summary>
    public int BaselineSeconds { get; init; } = 3600;

    /// <summary>Flagged seconds this close together are one alert.</summary>
    public TimeSpan MergeGap { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long the connection may be down before the admin is told the watch is blind. Silence of a blind watch looks like a calm
    /// market, which is the one thing it must never be taken for.
    /// </summary>
    public TimeSpan BlindAfter { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Names the rule, so alerts of different settings are never taken for each other. Change it when the logic changes.</summary>
    public string Version => string.Create(CultureInfo.InvariantCulture, $"s{WindowSeconds}-{AttentionMove}-{AlarmMove}-z{MinZScore}-v1");
}
