namespace Domain.ValueObjects;

/// <summary>
/// When a market is open, as a week that repeats: sessions given as offsets from Sunday 00:00 UTC (a Monday 08:00 UTC opening is
/// one day and eight hours). Always UTC; whoever builds it converts the broker's own time zone. Holidays are not part of it.
/// </summary>
public sealed class TradingHours
{
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    private readonly (TimeSpan Start, TimeSpan End)[] _sessions;

    /// <summary>Sessions may be given in any order and may touch or overlap; they are put in order and joined. At least one is needed.</summary>
    public TradingHours(IEnumerable<(TimeSpan Start, TimeSpan End)> sessions)
    {
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

    /// <summary>The stretches inside [<paramref name="from"/>, <paramref name="to"/>) in which the market is open, oldest first.</summary>
    public IEnumerable<(DateTime Start, DateTime End)> OpenRanges(DateTime from, DateTime to)
    {
        // The Sunday 00:00 on or before the start; DayOfWeek.Sunday is 0.
        var weekStart = from.Date.AddDays(-(int)from.DayOfWeek);

        for (; weekStart < to; weekStart += Week)
            foreach (var (start, end) in _sessions)
            {
                var s = weekStart + start;
                var e = weekStart + end;
                if (s < from) s = from;
                if (e > to) e = to;
                if (s < e)
                    yield return (s, e);
            }
    }
}
