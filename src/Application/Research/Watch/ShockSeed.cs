using Domain.Entities;

namespace Application.Research.Watch;

/// <summary>
/// Estimates how much the price usually moves in one second from the 1 minute candles already stored, so the watch can start judging
/// at once instead of listening for an hour first. A minute is 60 seconds, and the typical size of a move grows with the square root of
/// the time, so one second is a 60th of the variance of a minute.
/// <para>
/// It is an estimate: on a month of real futures trades this scaling came out about 14 % above the real figure on average (between 9 %
/// below and 39 % above for 90 % of the hours). Too high means the watch is a little less sensitive at first, which is the safe side.
/// </para>
/// </summary>
public static class ShockSeed
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <param name="candles">1 minute candles, oldest first.</param>
    /// <param name="now">Candles that end long before this say nothing about the market now.</param>
    /// <param name="maxAge">How old the newest candle may be.</param>
    /// <param name="minReturns">How many minute-to-minute returns are needed for an estimate worth using.</param>
    public static bool TryEstimate(IEnumerable<Candle> candles, DateTime now, TimeSpan maxAge, int minReturns, out double secondVolatility)
    {
        secondVolatility = 0;
        var ordered = candles.OrderBy(c => c.TimeRange.OpenTime).ToList();

        if (ordered.Count < 2 || now - ordered[^1].TimeRange.OpenTime > maxAge)
            return false;

        double sumSquares = 0;
        var count = 0;
        for (var i = 1; i < ordered.Count; i++)
        {
            // A hole in the candles would make one return span several minutes: left out.
            if (ordered[i].TimeRange.OpenTime - ordered[i - 1].TimeRange.OpenTime != Minute || ordered[i - 1].Close <= 0 || ordered[i].Close <= 0)
                continue;

            var r = Math.Log((double)(ordered[i].Close / ordered[i - 1].Close));
            sumSquares += r * r;
            count++;
        }

        if (count < minReturns || sumSquares <= 0)
            return false;

        secondVolatility = Math.Sqrt(sumSquares / count / 60);
        return true;
    }
}
