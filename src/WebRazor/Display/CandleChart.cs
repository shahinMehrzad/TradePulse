using System.Globalization;
using WebRazor.Api;

namespace WebRazor.Display;

/// <summary>One candle in chart coordinates. <c>Tooltip</c> is what the browser shows when the pointer rests on it.</summary>
public sealed record DrawnCandle(double X, double BodyY, double BodyWidth, double BodyHeight, double WickTop, double WickBottom, bool Up, string Tooltip);

/// <param name="Position">Where the label sits: a y coordinate for a price tick, an x coordinate for a time tick.</param>
public sealed record ChartTick(double Position, string Label);

public sealed record CandleChartModel(
    double Width, double Height, double PlotLeft, double PlotRight, double PlotTop, double PlotBottom,
    IReadOnlyList<DrawnCandle> Candles, IReadOnlyList<ChartTick> PriceTicks, IReadOnlyList<ChartTick> TimeTicks);

/// <summary>
/// Lays candles out as an SVG on the server, for the same reason the sparkline is (see <see cref="Sparkline"/>): the panel's content
/// security policy allows no inline script, and this needs none. The price scale covers exactly the candles shown.
/// </summary>
public static class CandleChart
{
    private const double Width = 800;
    private const double Height = 360;
    private const double Left = 8;
    private const double Right = 64;   // room for the price labels
    private const double Top = 12;
    private const double Bottom = 26;  // room for the time labels
    private const int PriceTickCount = 5;
    private const int TimeTickCount = 5;

    /// <param name="timeframe">e.g. "4h"; a day or longer is labelled with dates only.</param>
    public static CandleChartModel? Build(IReadOnlyList<ChartCandle> candles, string timeframe)
    {
        if (candles.Count == 0)
            return null;

        var plotRight = Width - Right;
        var plotBottom = Height - Bottom;
        var plotHeight = plotBottom - Top;

        var low = candles.Min(c => c.Low);
        var high = candles.Max(c => c.High);
        // A little air above and below; a flat market still gets a visible range.
        var padding = (high - low) == 0 ? Math.Max(high * 0.01m, 0.0001m) : (high - low) * 0.04m;
        var min = low - padding;
        var max = high + padding;

        double Y(decimal price) => Top + (double)((max - price) / (max - min)) * plotHeight;

        var slot = (plotRight - Left) / candles.Count;
        var bodyWidth = Math.Max(1, slot * 0.6);
        var dateOnly = timeframe.EndsWith('d') || timeframe.EndsWith('w');

        var drawn = candles.Select((c, i) =>
        {
            var x = Left + slot * (i + 0.5);
            var openY = Y(c.Open);
            var closeY = Y(c.Close);
            var tooltip = string.Create(CultureInfo.InvariantCulture,
                $"{c.OpenTime:yyyy-MM-dd HH:mm} UTC\nO {DashboardFormat.Price(c.Open)}  H {DashboardFormat.Price(c.High)}\nL {DashboardFormat.Price(c.Low)}  C {DashboardFormat.Price(c.Close)}\nVolume {c.Volume:N2}");

            return new DrawnCandle(x, Math.Min(openY, closeY), bodyWidth, Math.Max(1, Math.Abs(closeY - openY)), Y(c.High), Y(c.Low), c.Close >= c.Open, tooltip);
        }).ToList();

        var priceTicks = Enumerable.Range(0, PriceTickCount).Select(i =>
        {
            var price = min + (max - min) * i / (PriceTickCount - 1);
            return new ChartTick(Y(price), DashboardFormat.Price(price));
        }).ToList();

        var timeTicks = Enumerable.Range(0, Math.Min(TimeTickCount, candles.Count)).Select(i =>
        {
            var index = candles.Count == 1 ? 0 : (int)Math.Round((candles.Count - 1) * (double)i / (Math.Min(TimeTickCount, candles.Count) - 1));
            var time = candles[index].OpenTime;
            return new ChartTick(drawn[index].X, time.ToString(dateOnly ? "MM-dd" : "MM-dd HH:mm", CultureInfo.InvariantCulture));
        }).ToList();

        return new CandleChartModel(Width, Height, Left, plotRight, Top, plotBottom, drawn, priceTicks, timeTicks);
    }
}
