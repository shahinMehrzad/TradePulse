namespace BackgroundWorker.Collection.Candles;

/// <summary>
/// The short, frequent repair of the newest candles (section <c>GapRepair</c> of the worker's configuration). The full repair of a table stays
/// daily; this one only looks at the last <see cref="RecentWindowDays"/> days, once per <see cref="RecentPeriodMinutes"/>, so a candle that was
/// missed is filled within about an hour and not within a day.
/// </summary>
public sealed class GapRepairSettings
{
    public const string SectionName = "GapRepair";

    /// <summary>How far back the short repair looks.</summary>
    public int RecentWindowDays { get; set; } = 14;

    /// <summary>How often a table gets the short repair.</summary>
    public int RecentPeriodMinutes { get; set; } = 60;

    /// <summary>
    /// How many times the short repair asks the exchange about the same hole. A hole the exchange itself does not have must not be asked
    /// about for ever: after this many answers the daily repair is the only one that looks at it. A request that failed does not count.
    /// </summary>
    public int MaxAsksPerHole { get; set; } = 3;

    public TimeSpan RecentWindow => TimeSpan.FromDays(RecentWindowDays);

    public TimeSpan RecentPeriod => TimeSpan.FromMinutes(RecentPeriodMinutes);

    /// <summary>Throws at the start of the program for a value that makes no sense: a wrong setting must not be found out in the middle of the night.</summary>
    public GapRepairSettings Validate()
    {
        if (RecentWindowDays is < 1 or > 365)
            throw new InvalidOperationException($"{SectionName}:{nameof(RecentWindowDays)} must be 1 to 365.");
        if (RecentPeriodMinutes is < 5 or > 24 * 60)
            throw new InvalidOperationException($"{SectionName}:{nameof(RecentPeriodMinutes)} must be 5 to 1440.");
        if (MaxAsksPerHole is < 1 or > 100)
            throw new InvalidOperationException($"{SectionName}:{nameof(MaxAsksPerHole)} must be 1 to 100.");
        return this;
    }
}
