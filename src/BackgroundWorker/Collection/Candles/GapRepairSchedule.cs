using System.Collections.Concurrent;

namespace BackgroundWorker.Collection.Candles;

/// <summary>
/// What the gap repair remembers between its runs, per candle table, for as long as the process lives:
/// <list type="bullet">
/// <item><b>When the table was last repaired.</b> Looking for gaps scans the whole table, so it is done once a day per table (and
/// at once for every table after a socket reconnect). Because this is kept here and not in the loop that does the work, a loop that
/// is stopped and started again (the admin changed the series) does not scan the tables it already scanned.</item>
/// <item><b>That the exchange has nothing before a date.</b> A symbol listed after the configured start date has no candles
/// before its listing; once the exchange has answered empty for that stretch it is not asked again as long as the first stored
/// candle is the same.</item>
/// </list>
/// </summary>
public sealed class GapRepairSchedule(TimeProvider timeProvider)
{
    /// <summary>How long a table is left alone after a repair.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, DateTime> _repairedAt = new();
    private readonly ConcurrentDictionary<string, DateTime> _nothingBefore = new();

    public bool IsDue(string table) =>
        !_repairedAt.TryGetValue(table, out var at) || timeProvider.GetUtcNow().UtcDateTime - at >= Period;

    public void MarkRepaired(string table) => _repairedAt[table] = timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>True when the exchange was asked for the candles before <paramref name="firstCandle"/> and had none.</summary>
    public bool KnowsNothingBefore(string table, DateTime firstCandle) =>
        _nothingBefore.TryGetValue(table, out var known) && known == firstCandle;

    public void RememberNothingBefore(string table, DateTime firstCandle) => _nothingBefore[table] = firstCandle;
}
