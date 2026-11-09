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
/// <item><b>The short repair of the newest candles</b> (<see cref="GapRepairSettings"/>): when it last ran per table, and how many times the
/// exchange was asked about each hole. Without a limit a hole that the exchange does not have would be asked about every hour for ever.</item>
/// </list>
/// </summary>
public sealed class GapRepairSchedule(TimeProvider timeProvider, GapRepairSettings? settings = null)
{
    private readonly GapRepairSettings _settings = (settings ?? new GapRepairSettings()).Validate();
    private readonly ConcurrentDictionary<string, DateTime> _recentRepairedAt = new();
    private readonly ConcurrentDictionary<(string Table, DateTime From, DateTime To), int> _asks = new();
    private readonly ConcurrentDictionary<(string Table, DateTime From, DateTime To), byte> _confirmed = new();

    /// <summary>How long a table is left alone after a repair.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, (DateTime At, DateTime StartDate)> _repairedAt = new();
    private readonly ConcurrentDictionary<string, DateTime> _nothingBefore = new();

    /// <summary>
    /// Due when the table was never repaired, a day has passed, or the start date is not the one it was repaired for: an admin who
    /// moves it earlier wants that stretch filled now, not tomorrow.
    /// </summary>
    public bool IsDue(string table, DateTime startDate) =>
        !_repairedAt.TryGetValue(table, out var last)
        || last.StartDate != startDate
        || timeProvider.GetUtcNow().UtcDateTime - last.At >= Period;

    public void MarkRepaired(string table, DateTime startDate) =>
        _repairedAt[table] = (timeProvider.GetUtcNow().UtcDateTime, startDate);

    /// <summary>True when the exchange was asked for the candles before <paramref name="firstCandle"/> and had none.</summary>
    public bool KnowsNothingBefore(string table, DateTime firstCandle) =>
        _nothingBefore.TryGetValue(table, out var known) && known == firstCandle;

    public void RememberNothingBefore(string table, DateTime firstCandle) => _nothingBefore[table] = firstCandle;

    /// <summary>How many times the exchange is asked about one hole by the short repair.</summary>
    public int MaxAsksPerHole => _settings.MaxAsksPerHole;

    /// <summary>How often a table gets the short repair; also how long a time frame may be before its loop needs a wake-up of its own for it.</summary>
    public TimeSpan RecentPeriod => _settings.RecentPeriod;

    /// <summary>Due when the table never had one, or the period has passed. A full repair counts as one (<see cref="MarkRecentRepaired"/>).</summary>
    public bool IsRecentDue(string table) =>
        !_recentRepairedAt.TryGetValue(table, out var last) || timeProvider.GetUtcNow().UtcDateTime - last >= _settings.RecentPeriod;

    public void MarkRecentRepaired(string table) => _recentRepairedAt[table] = timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>Where the short repair starts looking: the window back from now, but never before the start date of the series.</summary>
    public DateTime RecentSince(DateTime startDate)
    {
        var since = timeProvider.GetUtcNow().UtcDateTime - _settings.RecentWindow;
        return since > startDate ? since : startDate;
    }

    /// <summary>False once the exchange has been asked about this very hole <see cref="GapRepairSettings.MaxAsksPerHole"/> times.</summary>
    public bool MayAsk(string table, DateTime from, DateTime to) =>
        !_asks.TryGetValue((table, from, to), out var asked) || asked < _settings.MaxAsksPerHole;

    /// <summary>Counts one answer of the exchange about a hole. Only a request that went through is an answer: call it for those only.</summary>
    public void RecordAsk(string table, DateTime from, DateTime to) => _asks.AddOrUpdate((table, from, to), 1, (_, n) => n + 1);

    /// <summary>
    /// Forgets the holes of the table that are not among <paramref name="holes"/> any more (filled), so the memory does not grow. Holes that end
    /// before <paramref name="since"/> are kept: a look at the newest candles does not know about them (the full scan does).
    /// </summary>
    public void ForgetAsksExcept(string table, IReadOnlyCollection<(DateTime From, DateTime To)> holes, DateTime? since = null)
    {
        bool Gone((string Table, DateTime From, DateTime To) key) =>
            key.Table == table && (since is null || key.To >= since) && !holes.Contains((key.From, key.To));

        foreach (var key in _asks.Keys.Where(Gone))
            _asks.TryRemove(key, out _);

        foreach (var key in _confirmed.Keys.Where(Gone))
            _confirmed.TryRemove(key, out _);
    }

    /// <summary>True once the hole is written down as missing on the exchange (see <see cref="Domain.Entities.ConfirmedCandleGap"/>), so it is written once per run.</summary>
    public bool IsConfirmed(string table, DateTime from, DateTime to) => _confirmed.ContainsKey((table, from, to));

    public void MarkConfirmed(string table, DateTime from, DateTime to) => _confirmed[(table, from, to)] = 0;

    /// <summary>How many times the hole was asked about (for the log and the tests).</summary>
    public int AsksAbout(string table, DateTime from, DateTime to) => _asks.GetValueOrDefault((table, from, to));
}
