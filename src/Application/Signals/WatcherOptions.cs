namespace Application.Signals;

/// <summary>How the paper trade watch keeps time. The defaults are what runs; tests give shorter ones.</summary>
public sealed record WatcherOptions
{
    /// <summary>How often the list of followed symbols is read again (a new listing, a symbol of a new trade).</summary>
    public TimeSpan SymbolRefresh { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How soon a subscription that failed is tried again.</summary>
    public TimeSpan SubscribeRetry { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the progress of the open trades is saved (a restart only has to look at what came after).</summary>
    public TimeSpan ProgressSave { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How soon a look at the past that could not be completed (the exchange said no) is tried again.</summary>
    public TimeSpan CatchUpRetry { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How soon the first read of the open trades is tried again when the database does not answer.</summary>
    public TimeSpan StartRetry { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long after a minute ends its candle is asked for: when the watch subscribes in the middle of a minute, the first half of that minute is
    /// seen by nobody, and its candle (asked for after it closes) covers it.
    /// </summary>
    public TimeSpan CandleDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Added before the moment a connection was lost, so the minute it was lost in is looked at whole.</summary>
    public TimeSpan CatchUpMargin { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>How many trades may wait to be looked at. When it is full the ones that do not fit are counted and the stretch is looked at again from the candles.</summary>
    public int QueueSize { get; init; } = 200_000;
}
