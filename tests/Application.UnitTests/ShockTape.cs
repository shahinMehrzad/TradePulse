using Application.Exchange;
using Application.Research.Watch;

namespace Application.UnitTests;

/// <summary>Made-up trades: one per second, a price that wobbles a very little all the time, plus the jumps a test asks for.</summary>
internal static class ShockTape
{
    public static readonly DateTime Start = new(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A short memory keeps the made-up series short; the rule is the same.</summary>
    public static readonly ShockWatchOptions Options = new()
    {
        WindowSeconds = 10,
        BaselineSeconds = 100,
        MergeGap = TimeSpan.FromSeconds(60),
    };

    public static LiveTrade Trade(int second, decimal price, decimal quantity = 2m) => new(Start.AddSeconds(second), price, quantity);

    /// <summary>Deterministic wobble of about +-0.003 %, so the usual size of a move is known and not zero.</summary>
    public static decimal Wobble(int second) => 100m * (1 + (((second * 37) % 7) - 3) * 0.00003m);

    /// <summary>The wobbling price with a permanent jump of <paramref name="jump"/> (0.01 is +1 %) from second <paramref name="at"/> on.</summary>
    public static decimal Price(int second, int at = int.MaxValue, decimal jump = 0m) => Wobble(second) * (second >= at ? 1 + jump : 1);

    /// <summary>Feeds one trade per second, from <paramref name="from"/> up to but not including <paramref name="to"/>.</summary>
    public static List<ShockSignal> Feed(ShockDetector detector, int from, int to, Func<int, decimal>? price = null)
    {
        var signals = new List<ShockSignal>();
        for (var second = from; second < to; second++)
            detector.Add(Trade(second, (price ?? Wobble)(second)), signals);
        return signals;
    }
}
