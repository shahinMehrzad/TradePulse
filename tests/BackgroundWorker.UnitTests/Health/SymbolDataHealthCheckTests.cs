using BackgroundWorker.Health;
using BackgroundWorker.Plans;
using BackgroundWorker.SymbolData;
using BackgroundWorker.UnitTests.SymbolData;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Health;

public class SymbolDataHealthCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private static Task<HealthCheckResult> Run(IHealthCheck check) => check.CheckHealthAsync(new HealthCheckContext(), Ct);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "the provider did not reach the expected state in time");
            await Task.Delay(5, Ct);
        }
    }

    [Fact]
    public async Task KnownPlansAreHealthy()
    {
        using var provider = await SymbolDataTestSupport.LoadedProviderAsync(_time, SymbolDataTestSupport.Binance());

        var result = await Run(new SymbolDataHealthCheck(provider));

        Assert.Equal((HealthStatus.Healthy, "Collecting symbol data for 1 symbols"), (result.Status, result.Description));
    }

    [Fact]
    public async Task AnEmptyTableIsHealthyBecauseCollectingOnlyCandlesIsAValidChoice()
    {
        using var provider = SymbolDataTestSupport.NewProvider(new FakeSymbolDataSource(), _time);
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == PlanStatus.WaitingForConfiguration);

        var result = await Run(new SymbolDataHealthCheck(provider));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("No funding rate or liquidation", result.Description);
    }

    [Fact]
    public async Task AnUnreadableDatabaseIsUnhealthyAndLeaksOnlyTheErrorType()
    {
        var source = new FakeSymbolDataSource { Failure = new InvalidOperationException("secret-host refused the connection") };
        using var provider = SymbolDataTestSupport.NewProvider(source, _time);
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == PlanStatus.Failed);

        var result = await Run(new SymbolDataHealthCheck(provider));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(nameof(InvalidOperationException), result.Description);
        Assert.DoesNotContain("secret", result.Description);
    }

    [Fact]
    public async Task AKnownListThatCannotBeRefreshedIsDegradedBecauseChangesAreNotSeen()
    {
        var source = new FakeSymbolDataSource();
        source.Plans.Add(SymbolDataTestSupport.Binance());
        using var provider = SymbolDataTestSupport.NewProvider(source, _time);
        await provider.StartAsync(Ct);
        await provider.WaitForPlansAsync(Ct);

        source.Failure = new InvalidOperationException("secret-host refused the connection");
        await WaitUntilAsync(() =>
        {
            _time.Advance(SymbolDataProvider.RefreshInterval);
            return provider.LastError is not null;
        });

        var result = await Run(new SymbolDataHealthCheck(provider));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("cannot be refreshed", result.Description);
        Assert.DoesNotContain("secret", result.Description);
    }

    [Fact]
    public async Task BeforeTheFirstReadFinishesItIsOnlyDegraded()
    {
        using var provider = SymbolDataTestSupport.NewProvider(new FakeSymbolDataSource(), _time);

        var result = await Run(new SymbolDataHealthCheck(provider));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        await Task.CompletedTask;
    }
}
