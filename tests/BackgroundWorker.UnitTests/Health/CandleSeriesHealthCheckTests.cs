using Domain.ValueObjects;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Plans;
using BackgroundWorker.Health;
using BackgroundWorker.UnitTests.CandleSeries;
using Domain.Enums;
using Infrastructure.Configurations;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Health;

public class CandleSeriesHealthCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));

    private static CandleSeriesPlan Series(MarketTypeEnum market) =>
        TestPlans.Binance(market, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);

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

    // ---- CandleSeriesHealthCheck ----

    [Fact]
    public async Task KnownSeriesAreHealthy()
    {
        using var provider = await CandleSeriesTestSupport.LoadedProviderAsync(_time, Series(MarketTypeEnum.Futures), Series(MarketTypeEnum.Spot));

        var result = await Run(new CandleSeriesHealthCheck(provider));

        Assert.Equal((HealthStatus.Healthy, "Collecting 2 candle series"), (result.Status, result.Description));
    }

    [Fact]
    public async Task AnEmptyTableIsDegradedBecauseTheCollectorIsIdle()
    {
        using var provider = CandleSeriesTestSupport.NewProvider(new FakeCandleSeriesSource(), _time);
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == PlanStatus.WaitingForConfiguration);

        var result = await Run(new CandleSeriesHealthCheck(provider));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("idle", result.Description);
    }

    [Fact]
    public async Task AnUnreadableDatabaseIsUnhealthyAndLeaksOnlyTheErrorType()
    {
        var source = new FakeCandleSeriesSource { Failure = new InvalidOperationException("secret-host refused the connection") };
        using var provider = CandleSeriesTestSupport.NewProvider(source, _time);
        await provider.StartAsync(Ct);
        await WaitUntilAsync(() => provider.Status == PlanStatus.Failed);

        var result = await Run(new CandleSeriesHealthCheck(provider));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(nameof(InvalidOperationException), result.Description);
        Assert.DoesNotContain("secret", result.Description);
    }

    [Fact]
    public async Task AKnownListThatCannotBeRefreshedIsDegradedNotUnhealthyBecauseCollectionGoesOn()
    {
        var source = new FakeCandleSeriesSource();
        source.Series.Add(Series(MarketTypeEnum.Futures));
        using var provider = CandleSeriesTestSupport.NewProvider(source, _time);
        await provider.StartAsync(Ct);
        await provider.WaitForSeriesAsync(Ct);

        source.Failure = new InvalidOperationException("secret-host refused the connection");
        await WaitUntilAsync(() =>
        {
            _time.Advance(CandleSeriesProvider.RefreshInterval);
            return provider.LastError is not null;
        });

        var result = await Run(new CandleSeriesHealthCheck(provider));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("cannot be refreshed", result.Description);
        Assert.Contains(nameof(InvalidOperationException), result.Description);
        Assert.DoesNotContain("secret", result.Description);

        source.Failure = null;
        await WaitUntilAsync(() =>
        {
            _time.Advance(CandleSeriesProvider.RefreshInterval);
            return provider.LastError is null;
        });
        Assert.Equal(HealthStatus.Healthy, (await Run(new CandleSeriesHealthCheck(provider))).Status);
    }

    [Fact]
    public async Task BeforeTheFirstReadFinishesItIsOnlyDegraded()
    {
        using var provider = CandleSeriesTestSupport.NewProvider(new FakeCandleSeriesSource(), _time);

        var result = await Run(new CandleSeriesHealthCheck(provider));

        Assert.Equal(HealthStatus.Degraded, result.Status);
        await Task.CompletedTask;
    }

    // ---- MarketGatedHealthCheck ----

    private sealed class FixedCheck(HealthStatus status) : IHealthCheck
    {
        public int Calls { get; private set; }

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new HealthCheckResult(status, "inner"));
        }
    }

    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task AMarketWithoutASeriesIsNotInUseAndTheInnerCheckIsNotEvenAsked()
    {
        using var provider = await CandleSeriesTestSupport.LoadedProviderAsync(_time, Series(MarketTypeEnum.Futures));
        var inner = new FixedCheck(HealthStatus.Unhealthy);

        var result = await Run(new MarketGatedHealthCheck(inner, provider, MarketTypeEnum.Spot, Grace, _time));

        Assert.Equal((HealthStatus.Healthy, 0), (result.Status, inner.Calls));
        Assert.Contains("Spot", result.Description);
    }

    [Fact]
    public async Task BeforeAnySeriesIsKnownNoMarketIsInUse()
    {
        using var provider = CandleSeriesTestSupport.NewProvider(new FakeCandleSeriesSource(), _time);
        var inner = new FixedCheck(HealthStatus.Unhealthy);

        var result = await Run(new MarketGatedHealthCheck(inner, provider, MarketTypeEnum.Futures, Grace, _time));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        await Task.CompletedTask;
    }

    [Theory]
    [InlineData(HealthStatus.Healthy)]
    [InlineData(HealthStatus.Degraded)]
    public async Task AMarketInUseReportsWhatTheInnerCheckSays(HealthStatus innerStatus)
    {
        using var provider = await CandleSeriesTestSupport.LoadedProviderAsync(_time, Series(MarketTypeEnum.Futures));
        _time.Advance(Grace + TimeSpan.FromMinutes(1));

        var result = await Run(new MarketGatedHealthCheck(new FixedCheck(innerStatus), provider, MarketTypeEnum.Futures, Grace, _time));

        Assert.Equal((innerStatus, "inner"), (result.Status, result.Description));
    }

    [Fact]
    public async Task AFreshlyOpenedConnectionIsGivenTimeBeforeItCountsAsUnhealthy()
    {
        // The streams are opened only once the series are known, which can be long after the process started.
        using var provider = await CandleSeriesTestSupport.LoadedProviderAsync(_time, Series(MarketTypeEnum.Futures));
        var gated = new MarketGatedHealthCheck(new FixedCheck(HealthStatus.Unhealthy), provider, MarketTypeEnum.Futures, Grace, _time);

        _time.Advance(TimeSpan.FromSeconds(30));
        var soon = await Run(gated);
        _time.Advance(Grace);
        var late = await Run(gated);

        Assert.Equal((HealthStatus.Degraded, HealthStatus.Unhealthy), (soon.Status, late.Status));
    }
}
