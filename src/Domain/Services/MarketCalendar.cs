using System.Collections.Concurrent;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.Services;

/// <summary>
/// Says which candles of a series are expected at all. Crypto never closes, but a broker's market does (forex over the weekend, an index
/// every night), and a candle that was never made is no hole in the data and no reason to call the series stale.
/// </summary>
public interface IMarketCalendar
{
    /// <summary>
    /// The parts of <paramref name="gap"/> (open times of missing candles, both ends included) in which the market was open, so the candles there
    /// should exist. Empty when the market was closed for all of it. A gap before the first candle is the start of the series and comes back as is.
    /// </summary>
    IReadOnlyList<CandleGap> ExpectedPart(CandleSeries series, CandleGap gap);
}

/// <summary>
/// The hours of the symbols that have some, as the exchange's adapter learned them. A symbol without hours is taken as always open: the worst
/// that costs is a report about a hole that is none, never a candle that is not fetched.
/// </summary>
public sealed class MarketCalendar : IMarketCalendar
{
    private readonly ConcurrentDictionary<(ExchangeEnum Exchange, MarketTypeEnum Market, string Symbol), TradingHours> _hours = new();

    /// <summary>The calendar of a market that never closes.</summary>
    public static IMarketCalendar AlwaysOpen { get; } = new MarketCalendar();

    /// <summary>Sets the hours of a symbol, or forgets them with <c>null</c>.</summary>
    public void SetHours(ExchangeEnum exchange, MarketTypeEnum market, ExchangeSymbol symbol, TradingHours? hours)
    {
        var key = (exchange, market, Normalize(symbol));
        if (hours is null)
            _hours.TryRemove(key, out _);
        else
            _hours[key] = hours;
    }

    public IReadOnlyList<CandleGap> ExpectedPart(CandleSeries series, CandleGap gap)
    {
        if (gap.BeforeFirstCandle || !_hours.TryGetValue((series.Exchange, series.MarketType, Normalize(series.Symbol)), out var hours))
            return [gap];

        var interval = series.Interval.Ticks;
        var first = gap.FirstMissingOpenTime;
        var lastIndex = (gap.LastMissingOpenTime - first).Ticks / interval;

        // A candle is expected when the market is open at any moment of it: open time t, closes at t + interval.
        var parts = new List<(long First, long Last)>();
        foreach (var (start, end) in hours.OpenRanges(first, gap.LastMissingOpenTime + series.Interval))
        {
            var firstIndex = (start - first).Ticks / interval;
            var lastOpenIndex = Math.Min(((end - first).Ticks + interval - 1) / interval - 1, lastIndex);

            if (parts.Count > 0 && firstIndex <= parts[^1].Last + 1)
                parts[^1] = (parts[^1].First, Math.Max(parts[^1].Last, lastOpenIndex));
            else
                parts.Add((firstIndex, lastOpenIndex));
        }

        return [.. parts.Select(p => new CandleGap(first.AddTicks(p.First * interval), first.AddTicks(p.Last * interval)))];
    }

    private static string Normalize(ExchangeSymbol symbol) => symbol.Value.ToUpperInvariant();
}
