using Application.Research;
using Domain.Entities;

namespace Application.UnitTests;

/// <summary>Made-up 1 minute candles: a price that wobbles a little all the time, plus the big moves a test asks for.</summary>
internal static class MinuteBars
{
    public static readonly DateTime Start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <param name="count">How many minutes.</param>
    /// <param name="moves">Extra return added at a minute (index), on top of the wobble, e.g. 0.015 for +1.5 %.</param>
    /// <param name="holeBefore">If set, the minutes from this index on are an hour later than they should be (a hole in the data).</param>
    public static List<MinuteBar> Build(int count, IReadOnlyDictionary<int, decimal>? moves = null, int? holeBefore = null)
    {
        var bars = new List<MinuteBar>(count);
        var price = 100m;

        for (var i = 0; i < count; i++)
        {
            // Deterministic wobble of about +-0.06 %, so the usual size of a move is known and not zero.
            var wobble = ((i * 37) % 7 - 3) * 0.0002m;
            price *= 1 + wobble + (moves is not null && moves.TryGetValue(i, out var move) ? move : 0);

            var time = Start.AddMinutes(i + (holeBefore is { } hole && i >= hole ? 60 : 0));
            var volume = moves is not null && moves.ContainsKey(i) ? 500m : 10m;
            bars.Add(new MinuteBar(time, price * 1.0001m, price * 0.9999m, price, volume));
        }

        return bars;
    }

    /// <summary>Three minutes in a row that each move by <paramref name="each"/>, starting at <paramref name="at"/>.</summary>
    public static Dictionary<int, decimal> Jump(int at, decimal each) =>
        new() { [at] = each, [at + 1] = each, [at + 2] = each };

    public static Candle ToCandle(MinuteBar bar) =>
        new(bar.OpenTime, bar.Close, bar.High, bar.Low, bar.Close, bar.Volume, bar.OpenTime.AddMinutes(1).AddMilliseconds(-1), bar.Close * bar.Volume, 100, bar.Volume / 2, bar.Close * bar.Volume / 2);
}
