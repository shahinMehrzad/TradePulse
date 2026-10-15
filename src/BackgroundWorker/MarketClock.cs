namespace BackgroundWorker;

/// <summary>Scheduling helpers shared by the collectors that wake up at interval boundaries.</summary>
public static class MarketClock
{
    /// <summary>Binance publishes a closed candle/period a moment after the boundary; wake up slightly later.</summary>
    public static readonly TimeSpan CloseOffset = TimeSpan.FromSeconds(3);

    /// <summary>Time from <paramref name="utcNow"/> until the next multiple of <paramref name="interval"/> (since the epoch) plus <paramref name="offset"/>.</summary>
    public static TimeSpan GetDelayUntilNextBoundary(TimeSpan interval, TimeSpan offset, DateTime utcNow)
    {
        var nextBoundary = new DateTime((utcNow.Ticks / interval.Ticks + 1) * interval.Ticks, DateTimeKind.Utc);
        return nextBoundary + offset - utcNow;
    }

    /// <summary>
    /// Runs <paramref name="sync"/> once immediately and then after every interval boundary until cancelled.
    /// <paramref name="sync"/> is responsible for handling its own failures; an exception ends the loop.
    /// </summary>
    public static async Task RunEveryIntervalAsync(TimeSpan interval, Func<CancellationToken, Task> sync, CancellationToken ct)
    {
        try
        {
            await sync(ct);
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(GetDelayUntilNextBoundary(interval, CloseOffset, DateTime.UtcNow), ct);
                await sync(ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
    }
}
