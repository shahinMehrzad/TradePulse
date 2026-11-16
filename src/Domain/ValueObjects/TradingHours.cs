namespace Domain.ValueObjects;

/// <summary>A stretch of time (UTC) in which a market is shut although its weekly hours say it is open: a holiday, an early close.</summary>
/// <param name="ThroughSession">
/// True for "the market closes early and stays shut for the rest of the day": the closure then lasts to the end of the weekly session it ends
/// in, instead of the market opening again for the few minutes the session still has left.
/// </param>
public readonly record struct TradingClosure(DateTime Start, DateTime End, bool ThroughSession = false);

/// <summary>
/// When a market is open, as a week that repeats: sessions given as offsets from Sunday 00:00 on the wall clock of a time zone (a Monday
/// 08:00 opening is one day and eight hours). The zone is the broker's own and has summer time, so the same session is an hour earlier
/// in UTC for half of the year; the default is UTC. Holidays are added with <see cref="WithClosures"/>.
/// </summary>
public sealed class TradingHours
{
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private readonly (TimeSpan Start, TimeSpan End)[] _sessions;
    private readonly TimeZoneInfo _zone;
    private readonly TradingClosure[] _closures;

    /// <summary>Sessions may be given in any order and may touch or overlap; they are put in order and joined. At least one is needed.</summary>
    /// <param name="sessions">Offsets from Sunday 00:00 in <paramref name="zone"/>.</param>
    /// <param name="zone">The time zone the sessions are given in; UTC when left out.</param>
    public TradingHours(IEnumerable<(TimeSpan Start, TimeSpan End)> sessions, TimeZoneInfo? zone = null)
    {
        _zone = zone ?? TimeZoneInfo.Utc;
        _closures = [];
        var ordered = sessions.OrderBy(s => s.Start).ToList();
        if (ordered.Count == 0)
            throw new ArgumentException("A market that is never open has no hours.", nameof(sessions));

        var joined = new List<(TimeSpan Start, TimeSpan End)>();
        foreach (var (start, end) in ordered)
        {
            if (start < TimeSpan.Zero || end > Week || end <= start)
                throw new ArgumentException($"A session must lie inside the week and end after it starts: {start} .. {end}.", nameof(sessions));

            if (joined.Count > 0 && start <= joined[^1].End)
                joined[^1] = (joined[^1].Start, TimeSpan.FromTicks(Math.Max(joined[^1].End.Ticks, end.Ticks)));
            else
                joined.Add((start, end));
        }

        _sessions = [.. joined];
    }

    private TradingHours(TradingHours hours, IEnumerable<TradingClosure> closures)
    {
        _sessions = hours._sessions;
        _zone = hours._zone;
        _closures = [.. closures.Where(c => c.End > c.Start).OrderBy(c => c.Start)];
    }

    /// <summary>The same hours with these closures taken out (the ones this had before are replaced).</summary>
    public TradingHours WithClosures(IEnumerable<TradingClosure> closures) => new(this, closures);

    /// <summary>The stretches inside [<paramref name="from"/>, <paramref name="to"/>) (UTC) in which the market is open, oldest first.</summary>
    public IEnumerable<(DateTime Start, DateTime End)> OpenRanges(DateTime from, DateTime to)
    {
        // The Sunday 00:00 on or before the start, on the wall clock of the zone; DayOfWeek.Sunday is 0.
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(from, DateTimeKind.Utc), _zone);
        var weekStart = DateTime.SpecifyKind(local.Date.AddDays(-(int)local.DayOfWeek), DateTimeKind.Unspecified);

        for (; ToUtc(weekStart) < to; weekStart += Week)
            foreach (var (start, end) in _sessions)
            {
                // The whole session first: a closure that ends in it may reach to its end, outside [from, to).
                foreach (var (s, e) in WithoutClosures(ToUtc(weekStart + start), ToUtc(weekStart + end)))
                {
                    var from2 = s < from ? from : s;
                    var to2 = e > to ? to : e;
                    if (from2 < to2)
                        yield return (from2, to2);
                }
            }
    }

    private IEnumerable<(DateTime Start, DateTime End)> WithoutClosures(DateTime sessionStart, DateTime sessionEnd)
    {
        var pieces = new List<(DateTime Start, DateTime End)> { (sessionStart, sessionEnd) };

        foreach (var closure in _closures)
        {
            if (closure.Start >= sessionEnd)
                break;   // ordered by start: nothing later reaches this session

            var end = closure.ThroughSession && closure.End > sessionStart && closure.End < sessionEnd ? sessionEnd : closure.End;
            if (end <= sessionStart)
                continue;

            var next = new List<(DateTime Start, DateTime End)>();
            foreach (var (s, e) in pieces)
            {
                if (closure.Start >= e || end <= s)
                {
                    next.Add((s, e));
                    continue;
                }

                if (s < closure.Start)
                    next.Add((s, closure.Start));
                if (end < e)
                    next.Add((end, e));
            }

            pieces = next;
        }

        return pieces;
    }

    // A wall-clock time that does not exist (the hour skipped when the clocks go forward) is the hour after it; one that exists twice is the first.
    private DateTime ToUtc(DateTime wallClock)
    {
        if (_zone.IsInvalidTime(wallClock))
            wallClock = wallClock.AddHours(1);

        return TimeZoneInfo.ConvertTimeToUtc(wallClock, _zone);
    }
}
