namespace Domain.ValueObjects
{
    /// <summary>
    /// A contiguous run of missing candles. Both bounds are open times and inclusive.
    /// </summary>
    public readonly record struct CandleGap(DateTime FirstMissingOpenTime, DateTime LastMissingOpenTime);
}
