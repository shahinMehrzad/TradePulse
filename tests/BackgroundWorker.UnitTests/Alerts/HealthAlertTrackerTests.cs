using BackgroundWorker.Alerts;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BackgroundWorker.UnitTests.Alerts;

public class HealthAlertTrackerTests
{
    private static HealthReport Report(params (string Name, HealthStatus Status, string? Description)[] entries) =>
        new(entries.ToDictionary(e => e.Name, e => new HealthReportEntry(e.Status, e.Description, TimeSpan.Zero, null, null)), TimeSpan.Zero);

    private static (string, HealthStatus, string?) Check(string name, HealthStatus status, string? description = null) => (name, status, description);

    [Fact]
    public void ACheckThatIsHealthyFromTheStartIsNeverAnnounced()
    {
        var tracker = new HealthAlertTracker(2);

        for (var i = 0; i < 5; i++)
            Assert.Null(tracker.Observe(Report(Check("database", HealthStatus.Healthy))));
    }

    [Fact]
    public void AChangeIsAnnouncedOnlyWhenTheSameStatusIsSeenInEnoughReportsInARow()
    {
        var tracker = new HealthAlertTracker(2);
        var degraded = Report(Check("candle-freshness", HealthStatus.Degraded, "2 series behind"));

        Assert.Null(tracker.Observe(degraded));
        var alert = tracker.Observe(degraded);

        Assert.NotNull(alert);
        Assert.Equal("Collector warning", alert.Title);
        Assert.Equal("⚠️ candle-freshness: degraded - 2 series behind", alert.Body);
        Assert.Null(tracker.Observe(degraded));   // already said
    }

    [Fact]
    public void ACheckThatFlickersSaysNothing()
    {
        var tracker = new HealthAlertTracker(2);
        var degraded = Report(Check("binance-futures-websocket", HealthStatus.Degraded, "silent"));
        var healthy = Report(Check("binance-futures-websocket", HealthStatus.Healthy));

        foreach (var report in new[] { degraded, healthy, degraded, healthy, degraded, healthy })
            Assert.Null(tracker.Observe(report));
    }

    [Fact]
    public void BeingBackIsAnnouncedOnceAfterTheSameConfirmation()
    {
        var tracker = new HealthAlertTracker(2);
        var down = Report(Check("database", HealthStatus.Unhealthy, "Cannot connect"));
        var up = Report(Check("database", HealthStatus.Healthy));
        tracker.Observe(down);
        Assert.Equal("Collector problem", tracker.Observe(down)!.Title);

        Assert.Null(tracker.Observe(up));
        var back = tracker.Observe(up);

        Assert.Equal(("Collector is healthy again", "✅ database: healthy again"), (back!.Title, back.Body));
        Assert.Null(tracker.Observe(up));
    }

    [Fact]
    public void GettingWorseIsAnnouncedAgain()
    {
        var tracker = new HealthAlertTracker(2);
        var degraded = Report(Check("candle-freshness", HealthStatus.Degraded, "1 behind"));
        var unhealthy = Report(Check("candle-freshness", HealthStatus.Unhealthy, "3 behind"));
        tracker.Observe(degraded);
        tracker.Observe(degraded);

        Assert.Null(tracker.Observe(unhealthy));
        var alert = tracker.Observe(unhealthy);

        Assert.Equal(("Collector problem", "🔴 candle-freshness: unhealthy - 3 behind"), (alert!.Title, alert.Body));
    }

    [Fact]
    public void ChecksThatChangeInTheSameReportShareOneMessageInAStableOrder()
    {
        var tracker = new HealthAlertTracker(1);

        var alert = tracker.Observe(Report(
            Check("database", HealthStatus.Unhealthy, "Cannot connect"),
            Check("binance-futures-rest", HealthStatus.Degraded, "slow"),
            Check("candle-series", HealthStatus.Healthy)));

        Assert.Equal("Collector problem", alert!.Title);
        Assert.Equal("⚠️ binance-futures-rest: degraded - slow\n🔴 database: unhealthy - Cannot connect", alert.Body);
    }

    [Fact]
    public void EachCheckIsJudgedOnItsOwnSoANewProblemIsNotHiddenByAnOldOne()
    {
        var tracker = new HealthAlertTracker(1);
        tracker.Observe(Report(Check("database", HealthStatus.Unhealthy, "down"), Check("candle-freshness", HealthStatus.Healthy)));

        var alert = tracker.Observe(Report(Check("database", HealthStatus.Unhealthy, "down"), Check("candle-freshness", HealthStatus.Degraded, "1 behind")));

        Assert.Equal("⚠️ candle-freshness: degraded - 1 behind", alert!.Body);   // the database is not said again
    }

    [Fact]
    public void AConfirmationOfOneAnnouncesAtOnce()
    {
        var tracker = new HealthAlertTracker(1);

        Assert.NotNull(tracker.Observe(Report(Check("database", HealthStatus.Unhealthy, "down"))));
    }
}
