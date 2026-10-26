using System.Collections.Concurrent;
using Domain.ValueObjects;

namespace BackgroundWorker.Health;

/// <summary>What is still missing from one candle table after the last gap repair.</summary>
/// <param name="Gaps">How many separate holes are left. 0 means the table was looked at and is complete.</param>
/// <param name="MissingCandles">How many candles are missing in all of them.</param>
/// <param name="Oldest">The open time of the first missing candle (UTC), or null when nothing is missing.</param>
public readonly record struct GapSummary(int Gaps, long MissingCandles, DateTime? Oldest);

/// <summary>
/// The gaps the candle workers could not fill, per table, as of each table's last repair (at startup, every 24 h and after a socket
/// reconnect). Holes the exchange itself has no candles for (maintenance, no trades) stay open for good and are only reported
/// here and in the log; nobody retries them in a loop. Written by the repair after it has done its work, read by the health
/// check: a few dictionary writes, safe to call from the collection path.
/// </summary>
public sealed class OpenGaps
{
    private readonly ConcurrentDictionary<string, GapSummary> _byTable = new();

    /// <summary>Records what is left open in <paramref name="table"/>; an empty list records that it was checked and is complete.</summary>
    public void Set(string table, IReadOnlyList<CandleGap> gaps, TimeSpan interval)
    {
        var missing = gaps.Sum(g => (long)((g.LastMissingOpenTime - g.FirstMissingOpenTime) / interval) + 1);
        _byTable[table] = new GapSummary(gaps.Count, missing, gaps.Count == 0 ? null : gaps.Min(g => g.FirstMissingOpenTime));
    }

    /// <summary>The summary of a table, or null when its gaps were never looked at.</summary>
    public GapSummary? Get(string table) => _byTable.TryGetValue(table, out var summary) ? summary : null;
}
