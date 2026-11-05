using System.Globalization;

namespace Application.Research.Watch;

/// <summary>
/// The rule that decides what counts as a shock. Two checks run on every symbol, each on its own, and each says how many times bigger the
/// price move is than what the symbol usually does in that time (<c>z</c>):
/// <list type="bullet">
/// <item><b>Sudden:</b> the move over <see cref="WindowSeconds"/> is at least <see cref="MinZScore"/> times usual (a crash, a squeeze).</item>
/// <item><b>Gradual:</b> the move over <see cref="GradualWindowMinutes"/> minutes is at least <see cref="GradualMinZScore"/> times usual (a market
/// that slides for ten minutes, which the sudden check does not see).</item>
/// </list>
/// "Usual" is the larger of what the last hour did and a share (<see cref="DayBaselineShare"/>) of what the last 24 hours did, so a quiet night
/// does not make a small move look huge. It is the symbol's own, so a heavy coin and a jumpy one need no settings of their own.
/// A move under <see cref="MinMove"/> is never a shock, however quiet the symbol was.
/// </summary>
public sealed record ShockWatchOptions
{
    /// <summary>The last hour, in minutes: what the gradual check compares with, and what the usual size of a second is estimated from at the start.</summary>
    public const int HourMinutes = 60;

    /// <summary>The window of the sudden check, in seconds.</summary>
    public int WindowSeconds { get; init; } = 30;

    /// <summary>How many times its usual size a move over <see cref="WindowSeconds"/> must be (the sudden check).</summary>
    public double MinZScore { get; init; } = 20;

    /// <summary>The window of the gradual check, in minutes.</summary>
    public int GradualWindowMinutes { get; init; } = 15;

    /// <summary>
    /// How many times its usual size a move over <see cref="GradualWindowMinutes"/> must be (the gradual check). 4.5 is about 2.6 alerts a week
    /// per symbol over a year and catches the slide of 29 Oct 2026 in 12 of 14 symbols; <b>5.0</b> is the quieter choice (about 1.4 a week, and only 4
    /// of 14 in that slide). Change it here, or with <c>Watch:Rule:GradualMinZScore</c>.
    /// </summary>
    public double GradualMinZScore { get; init; } = 4.5;

    /// <summary>The smallest move, as a logarithmic return, that can be a shock (0.0015 is about 0.15 %).</summary>
    public double MinMove { get; init; } = 0.0015;

    /// <summary>How much of the last 24 hours' usual size counts as a floor under the last hour's (0.5 is half of it).</summary>
    public double DayBaselineShare { get; init; } = 0.5;

    /// <summary>How many seconds before describe what is usual for the sudden check (3600 is one hour). The watch is silent until it has seen that many.</summary>
    public int BaselineSeconds { get; init; } = 3600;

    /// <summary>How many minutes make the 24 hour baseline. It is used once a quarter of it has been seen.</summary>
    public int DayBaselineMinutes { get; init; } = 1440;

    /// <summary>Flagged seconds this close together are one sudden alert.</summary>
    public TimeSpan MergeGap { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Flagged minutes this close together are one gradual alert.</summary>
    public TimeSpan GradualMergeGap { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>After a message about a symbol, no other message about it for this long. The alerts are still stored; only the message is held back.</summary>
    public TimeSpan SilenceAfterMessage { get; init; } = TimeSpan.FromHours(12);

    /// <summary>
    /// How many of the latest stored 1 minute candles the watch starts from: they estimate the usual size of a move, so it need not listen for a
    /// whole baseline first, and they are the 24 hour baseline of the gradual check. It must cover <see cref="DayBaselineMinutes"/>. The collector
    /// of that symbol must be running at the 1 minute time frame.
    /// </summary>
    public int SeedMinutes { get; init; } = 1500;

    /// <summary>Fewer minute-to-minute returns than this are too few to estimate from.</summary>
    public int SeedMinReturns { get; init; } = 30;

    /// <summary>If the newest stored candle is older than this, the collector is not keeping up and the candles say nothing about now.</summary>
    public TimeSpan SeedMaxAge { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long the connection may be down before the admin is told the watch is blind. Silence of a blind watch looks like a calm
    /// market, which is the one thing it must never be taken for.
    /// </summary>
    public TimeSpan BlindAfter { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How often the watch looks again at which symbols it should follow, and adds or drops the ones that changed.</summary>
    public TimeSpan WatchListRefresh { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Names the sudden check, so alerts of different settings are never taken for each other. Change it when the logic changes.</summary>
    public string SuddenVersion => string.Create(CultureInfo.InvariantCulture, $"s{WindowSeconds}-z{MinZScore}-v2");

    /// <summary>Names the gradual check. It differs from <see cref="SuddenVersion"/>, so two alerts that start in the same second are two alerts.</summary>
    public string GradualVersion => string.Create(CultureInfo.InvariantCulture, $"m{GradualWindowMinutes}-z{GradualMinZScore}-v2");
}
