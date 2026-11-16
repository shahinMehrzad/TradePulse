using BackgroundWorker.Alerts;
using BackgroundWorker.Health;
using Infrastructure.Exchange.Ctrader;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Health;

public class CtraderTokenHealthCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 12, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(Now);

    private Task<HealthCheckResult> Run(DateTimeOffset? expiresAt, TimeSpan? warnBefore = null)
    {
        var settings = new CtraderExchangeConfigurations { AccessTokenExpiresAt = expiresAt, AccessToken = "the-secret-token" };
        if (warnBefore is { } warn)
            settings.AccessTokenWarnBefore = warn;

        return new CtraderTokenHealthCheck(Options.Create(settings), _time).CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ATokenWithManyDaysLeftIsHealthy()
    {
        var result = await Run(Now.AddDays(20));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("20 days", result.Description);
    }

    [Fact]
    public async Task ADayBeforeTheEndTheCheckTurnsDegradedAndSaysWhatToDo()
    {
        var result = await Run(Now.AddHours(23));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("23 hours", result.Description);
        Assert.Contains("ctrader-check refresh", result.Description);
        Assert.Contains("restart collector and api", result.Description);
    }

    [Fact]
    public async Task ExactlyTheWarningPeriodBeforeTheEndIsAlreadyDegradedAndJustOverItIsNot()
    {
        Assert.Equal(HealthStatus.Degraded, (await Run(Now.AddDays(1))).Status);
        Assert.Equal(HealthStatus.Healthy, (await Run(Now.AddDays(1).AddMinutes(1))).Status);
    }

    [Fact]
    public async Task TheWarningPeriodCanBeLonger()
    {
        // Three days leaves room for a weekend.
        Assert.Equal(HealthStatus.Degraded, (await Run(Now.AddDays(2), warnBefore: TimeSpan.FromDays(3))).Status);
        Assert.Equal(HealthStatus.Healthy, (await Run(Now.AddDays(4), warnBefore: TimeSpan.FromDays(3))).Status);
    }

    [Fact]
    public async Task ATokenThatHasRunOutIsUnhealthy()
    {
        var result = await Run(Now.AddMinutes(-1));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("ran out", result.Description);
    }

    [Fact]
    public async Task WithNoEndGivenItIsHealthyAndSaysNobodyWillBeWarned()
    {
        var result = await Run(expiresAt: null);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("CTRADER_ACCESS_TOKEN_EXPIRES_AT", result.Description);
    }

    [Fact]
    public async Task NothingItSaysHoldsTheToken()
    {
        foreach (var end in new DateTimeOffset?[] { Now.AddDays(20), Now.AddHours(5), Now.AddMinutes(-5), null })
        {
            var result = await Run(end);

            Assert.DoesNotContain("the-secret-token", result.Description);
            Assert.All(result.Data.Values, value => Assert.DoesNotContain("the-secret-token", value.ToString()));
        }
    }

    [Fact]
    public async Task TheAlertChannelsAreToldOnceADayBeforeAndAgainWhenANewTokenIsIn()
    {
        var tracker = new HealthAlertTracker(confirmations: 2);
        HealthReport Report(HealthCheckResult result) => new(
            new Dictionary<string, HealthReportEntry> { ["ctrader-token"] = new(result.Status, result.Description, TimeSpan.Zero, null, result.Data) }, TimeSpan.Zero);

        // Well before the end: nothing to say, however many reports there are.
        Assert.Null(tracker.Observe(Report(await Run(Now.AddDays(10)))));
        Assert.Null(tracker.Observe(Report(await Run(Now.AddDays(10)))));

        // A day before: the second report that says so is the message, and the third says nothing more.
        _time.Advance(TimeSpan.FromDays(9).Add(TimeSpan.FromHours(1)));
        var expiry = Now.AddDays(10);
        Assert.Null(tracker.Observe(Report(await Run(expiry))));
        var warning = tracker.Observe(Report(await Run(expiry)));
        Assert.NotNull(warning);
        Assert.Contains("ctrader-token", warning.Body);
        Assert.Contains("23 hours", warning.Body);
        Assert.Null(tracker.Observe(Report(await Run(expiry))));

        // A new token has been given and the collector restarted.
        var renewed = _time.GetUtcNow().AddDays(30);
        Assert.Null(tracker.Observe(Report(await Run(renewed))));
        var back = tracker.Observe(Report(await Run(renewed)));
        Assert.NotNull(back);
        Assert.Contains("ctrader-token: healthy again", back.Body);
    }
}
