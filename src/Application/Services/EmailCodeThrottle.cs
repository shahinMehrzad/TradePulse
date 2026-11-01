namespace Application.Services;

/// <summary>
/// Keeps the sign-in mails from being a way to flood somebody's mailbox: at least a minute between two codes for a user and at most
/// five an hour. The test code of turning email codes on and the codes of signing in are counted apart, so a user who has just turned
/// them on can sign in with one. In memory, so a restart forgets it; the login rate limit per IP still stands in front.
/// </summary>
public sealed class EmailCodeThrottle(TimeProvider time)
{
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(1);
    public const int MaxPerHour = 5;

    private readonly Dictionary<(Guid UserId, bool ForEnabling), List<DateTimeOffset>> _sent = [];
    private readonly Lock _lock = new();

    /// <returns>True when a code may be sent now (and counts it as sent).</returns>
    public bool TryAcquire(Guid userId, bool forEnabling)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            foreach (var key in _sent.Where(e => e.Value.All(t => now - t >= TimeSpan.FromHours(1))).Select(e => e.Key).ToList())
                _sent.Remove(key);

            if (!_sent.TryGetValue((userId, forEnabling), out var times))
                _sent[(userId, forEnabling)] = times = [];

            if (times.Count >= MaxPerHour || (times.Count > 0 && now - times[^1] < MinimumGap))
                return false;

            times.Add(now);
            return true;
        }
    }
}
