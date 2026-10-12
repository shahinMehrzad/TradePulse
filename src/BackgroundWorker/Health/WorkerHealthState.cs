namespace BackgroundWorker.Health;

/// <summary>Names of the monitored components, shared by the workers that report and the checks that read.</summary>
public static class HealthComponents
{
    public const string FuturesRest = "binance-futures-rest";
    public const string SpotRest = "binance-spot-rest";
    public const string FuturesWebSocket = "binance-futures-websocket";
    public const string LiquidationStream = "binance-liquidation-stream";
    public const string CandleRequests = "candle-requests";
    public const string CandleStreamFutures = "binance-candle-stream-futures";
    public const string CandleStreamSpot = "binance-candle-stream-spot";
    public const string CandleFreshness = "candle-freshness";
    public const string FundingRateRequests = "funding-rate-requests";
    public const string OpenInterestRequests = "open-interest-requests";
}

/// <summary>Immutable view of what has been observed about one component.</summary>
public sealed record ComponentSnapshot(
    string Name,
    int ConsecutiveFailures,
    DateTime? LastSuccessAt,
    DateTime? LastFailureAt,
    string? LastError,
    bool? Connected,
    DateTime? ConnectionChangedAt,
    DateTime? LastMessageAt,
    long MessageCount);

/// <summary>
/// Thread-safe scoreboard the workers write to and the health checks read from. Reporting is a few field writes
/// under a lock, so it is safe to call from the collection hot path and from socket callbacks.
/// </summary>
public sealed class WorkerHealthState(TimeProvider timeProvider)
{
    private readonly Dictionary<string, Entry> _entries = [];
    private readonly Lock _gate = new();

    /// <summary>When monitoring began; startup grace periods are measured from here.</summary>
    public DateTime StartedAt { get; } = timeProvider.GetUtcNow().UtcDateTime;

    public void RecordSuccess(string component) => Update(component, e =>
    {
        e.ConsecutiveFailures = 0;
        e.LastSuccessAt = Now();
    });

    public void RecordFailure(string component, string? error) => Update(component, e =>
    {
        e.ConsecutiveFailures++;
        e.LastFailureAt = Now();
        e.LastError = error;
    });

    /// <summary>Reports a connection going up or down. Repeating the current state does not reset the timestamp.</summary>
    public void SetConnected(string component, bool connected) => Update(component, e =>
    {
        if (e.Connected == connected)
            return;

        e.Connected = connected;
        e.ConnectionChangedAt = Now();
    });

    public void RecordMessage(string component) => Update(component, e =>
    {
        e.LastMessageAt = Now();
        e.MessageCount++;
    });

    public ComponentSnapshot Get(string component)
    {
        lock (_gate)
        {
            var e = _entries.GetValueOrDefault(component) ?? new Entry();
            return new ComponentSnapshot(component, e.ConsecutiveFailures, e.LastSuccessAt, e.LastFailureAt, e.LastError,
                e.Connected, e.ConnectionChangedAt, e.LastMessageAt, e.MessageCount);
        }
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private void Update(string component, Action<Entry> change)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(component, out var entry))
                _entries[component] = entry = new Entry();

            change(entry);
        }
    }

    private sealed class Entry
    {
        public int ConsecutiveFailures;
        public DateTime? LastSuccessAt;
        public DateTime? LastFailureAt;
        public string? LastError;
        public bool? Connected;
        public DateTime? ConnectionChangedAt;
        public DateTime? LastMessageAt;
        public long MessageCount;
    }
}
