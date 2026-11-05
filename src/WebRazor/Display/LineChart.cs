using System.Globalization;

namespace WebRazor.Display;

public sealed record LineChartPoint(double X, double Y, string Tooltip);

/// <summary>A label at a place on an axis (a Y for the value axis, an X for the time axis).</summary>
public sealed record LineChartTick(double Position, string Label);

/// <summary>Everything a view needs to draw one line chart as plain SVG: the panel's content security policy leaves no room for a chart script.</summary>
public sealed class LineChartModel
{
    public double Width { get; init; }
    public double Height { get; init; }
    public double PlotLeft { get; init; }
    public double PlotRight { get; init; }
    public double PlotTop { get; init; }
    public double PlotBottom { get; init; }

    /// <summary>What a screen reader is told the chart is.</summary>
    public string Label { get; init; } = "Line chart";

    public required IReadOnlyList<LineChartPoint> Points { get; init; }

    /// <summary>The <c>points</c> of the polyline.</summary>
    public required string Line { get; init; }

    public required IReadOnlyList<LineChartTick> ValueTicks { get; init; }
    public required IReadOnlyList<LineChartTick> TimeTicks { get; init; }

    /// <summary>Where the reference value (1 for a ratio: as many on one side as on the other) falls, or null when there is none.</summary>
    public double? ReferenceY { get; init; }
}

/// <summary>Turns a time series into a <see cref="LineChartModel"/>, the way <see cref="CandleChart"/> does for candles.</summary>
public static class LineChart
{
    public const double Width = 520;
    public const double Height = 170;
    private const double Left = 6;
    private const double RightMargin = 52;
    private const double Top = 10;
    private const double BottomMargin = 26;

    /// <param name="series">Oldest first, UTC.</param>
    /// <param name="timeframe">e.g. <c>1h</c>, for how the times on the axis are written.</param>
    /// <param name="valueFormat">A .NET numeric format for the values ("0.00").</param>
    /// <param name="reference">A value that is always in view and gets a line of its own (1 for a ratio).</param>
    /// <param name="label">What a screen reader is told the chart is.</param>
    /// <returns>Null when there are fewer than two points: a single value is no line.</returns>
    public static LineChartModel? Build(IReadOnlyList<(DateTime Time, decimal Value)> series, string timeframe, string valueFormat, decimal? reference = null, string label = "Line chart")
    {
        if (series.Count < 2)
            return null;

        var values = series.Select(p => p.Value).ToList();
        var low = values.Min();
        var high = values.Max();
        if (reference is { } r)
        {
            low = Math.Min(low, r);
            high = Math.Max(high, r);
        }

        // Some air above and below the line, and a flat series is a line through the middle.
        var pad = (high - low) * 0.08m;
        var lowEdge = low - pad;
        var highEdge = high + pad;

        var plotRight = Width - RightMargin;
        var plotBottom = Height - BottomMargin;
        double Y(decimal value) =>
            highEdge == lowEdge ? (Top + plotBottom) / 2 : Top + (double)((highEdge - value) / (highEdge - lowEdge)) * (plotBottom - Top);

        var step = (plotRight - Left) / (series.Count - 1);
        var points = series
            .Select((p, i) => new LineChartPoint(Left + i * step, Y(p.Value),
                $"{DashboardFormat.ChartTime(p.Time, timeframe)} UTC: {p.Value.ToString(valueFormat, CultureInfo.InvariantCulture)}"))
            .ToList();

        var mid = (low + high) / 2;
        var valueTicks = new[] { high, mid, low }
            .Distinct()
            .Select(v => new LineChartTick(Y(v), v.ToString(valueFormat, CultureInfo.InvariantCulture)))
            .ToList();

        var timeTicks = new[] { 0, series.Count / 2, series.Count - 1 }
            .Distinct()
            .Select(i => new LineChartTick(points[i].X, DashboardFormat.ChartTime(series[i].Time, timeframe)))
            .ToList();

        return new LineChartModel
        {
            Label = label,
            Width = Width, Height = Height, PlotLeft = Left, PlotRight = plotRight, PlotTop = Top, PlotBottom = plotBottom,
            Points = points,
            Line = string.Join(' ', points.Select(p => $"{Format(p.X)},{Format(p.Y)}")),
            ValueTicks = valueTicks,
            TimeTicks = timeTicks,
            ReferenceY = reference is { } line ? Y(line) : null,
        };
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
