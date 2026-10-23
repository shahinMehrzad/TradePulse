using System.Globalization;

namespace WebRazor.Display;

/// <summary>
/// Turns a list of prices into the points of a small SVG chart, so a card's sparkline is drawn on the server: no script, which the
/// panel's content security policy would only allow from the site itself anyway.
/// </summary>
public static class Sparkline
{
    public const double Width = 200;
    public const double Height = 52;
    private const double Padding = 4;

    /// <returns>
    /// <c>Line</c> for a polyline and <c>Area</c> for the filled shape under it, both in a <c>Width</c> x <c>Height</c> box with the
    /// highest price at the top. A flat series is a line through the middle; no prices give empty strings.
    /// </returns>
    public static (string Line, string Area) Build(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
            return ("", "");

        // One price still has to be a line.
        IReadOnlyList<decimal> points = values.Count == 1 ? [values[0], values[0]] : values;

        var min = points.Min();
        var max = points.Max();
        var span = max - min;
        var step = Width / (points.Count - 1);

        var line = string.Join(' ', points.Select((v, i) =>
        {
            var y = span == 0 ? Height / 2 : Padding + (double)((max - v) / span) * (Height - 2 * Padding);
            return $"{Format(i * step)},{Format(y)}";
        }));

        return (line, $"{line} {Format(Width)},{Format(Height)} 0,{Format(Height)}");
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
