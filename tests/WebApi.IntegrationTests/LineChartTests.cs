using WebRazor.Display;

namespace WebApi.IntegrationTests;

/// <summary>The line charts of the series page (<see cref="LineChart"/>, compiled into this project like the Series list's query): the numbers a drawing is made of.</summary>
public class LineChartTests
{
    private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (DateTime Time, decimal Value)[] Hourly(params decimal[] values) =>
        [.. values.Select((v, i) => (Start.AddHours(i), v))];

    [Fact]
    public void FewerThanTwoPointsIsNoLine()
    {
        Assert.Null(LineChart.Build([], "1h", "0.00"));
        Assert.Null(LineChart.Build(Hourly(1.2m), "1h", "0.00"));
    }

    [Fact]
    public void TheFirstPointIsAtTheLeftEdgeTheLastAtTheRightEdgeOfThePlot()
    {
        var chart = LineChart.Build(Hourly(1m, 2m, 3m, 2m), "1h", "0.00")!;

        Assert.Equal(chart.PlotLeft, chart.Points[0].X, precision: 6);
        Assert.Equal(chart.PlotRight, chart.Points[^1].X, precision: 6);
        Assert.True(chart.Points.Zip(chart.Points.Skip(1)).All(pair => pair.First.X < pair.Second.X));
    }

    [Fact]
    public void AHigherValueIsHigherOnTheScreenAndEveryPointStaysInsideThePlot()
    {
        var chart = LineChart.Build(Hourly(1m, 5m, 3m), "1h", "0.00")!;

        Assert.True(chart.Points[1].Y < chart.Points[2].Y);   // 5 is above 3 (a smaller Y is nearer the top)
        Assert.True(chart.Points[2].Y < chart.Points[0].Y);   // and 3 above 1
        Assert.All(chart.Points, p => Assert.InRange(p.Y, chart.PlotTop, chart.PlotBottom));
    }

    [Fact]
    public void AFlatSeriesIsALineThroughTheMiddleNotADivisionByZero()
    {
        var chart = LineChart.Build(Hourly(1.5m, 1.5m, 1.5m), "1h", "0.00")!;

        var middle = (chart.PlotTop + chart.PlotBottom) / 2;
        Assert.All(chart.Points, p => Assert.Equal(middle, p.Y, precision: 6));
        Assert.Single(chart.ValueTicks);
    }

    [Fact]
    public void TheReferenceIsAlwaysInViewEvenWhenEveryValueIsOnOneSideOfIt()
    {
        var chart = LineChart.Build(Hourly(1.2m, 1.4m, 1.3m), "1h", "0.000", reference: 1m)!;

        Assert.NotNull(chart.ReferenceY);
        Assert.InRange(chart.ReferenceY!.Value, chart.PlotTop, chart.PlotBottom);
        Assert.All(chart.Points, p => Assert.True(p.Y < chart.ReferenceY));   // every value is above 1
    }

    [Fact]
    public void WithoutAReferenceThereIsNoReferenceLine() =>
        Assert.Null(LineChart.Build(Hourly(1m, 2m), "1h", "0.00")!.ReferenceY);

    [Fact]
    public void AValueIsToldInItsTooltipWithTheTimeAndTheFormatAskedFor()
    {
        var chart = LineChart.Build(Hourly(1.23456m, 2m), "1h", "0.000")!;

        Assert.Equal("10-01 00:00 UTC: 1.235", chart.Points[0].Tooltip);
    }

    [Fact]
    public void TheTimeAxisHasTheFirstMiddleAndLastTimeAndTheValueAxisTheHighestMiddleAndLowest()
    {
        var chart = LineChart.Build(Hourly(1m, 2m, 3m, 4m, 5m), "1h", "0")!;

        Assert.Equal(["10-01 00:00", "10-01 02:00", "10-01 04:00"], chart.TimeTicks.Select(t => t.Label));
        Assert.Equal(["5", "3", "1"], chart.ValueTicks.Select(t => t.Label));
    }

    [Fact]
    public void ThePolylineHasOnePairPerPoint()
    {
        var chart = LineChart.Build(Hourly(1m, 2m, 3m), "1h", "0")!;

        Assert.Equal(3, chart.Line.Split(' ').Length);
        Assert.DoesNotContain(",,", chart.Line);
    }
}
