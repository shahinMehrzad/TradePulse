using BackgroundWorker.Health;
using BackgroundWorker.UnitTests.CandleSeries;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Domain.ValueObjects;
using Infrastructure.Configurations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Health;

public class CandleFreshnessTests
{
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly DateTime Noon = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Noon.AddMinutes(5)));
    private readonly FakeCandleRepository _repository = new();

    private sealed class FakeCandleRepository : ICandleRepository
    {
        public DateTime? Latest { get; set; }
        public bool Throw { get; set; }
        public int Calls { get; private set; }

        public Task<DateTimeOffset?> GetFirstOpenTimeAsync(string tableName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DateTimeOffset?> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw)
                throw new InvalidOperationException("secret-host unreachable");
            return Task.FromResult<DateTimeOffset?>(Latest is { } l ? new DateTimeOffset(l) : null);
        }

        public Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> BulkInsert(string tableName, IEnumerable<Candle> candles, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Candle>> GetPageAsync(string tableName, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Candle>> GetLatestAsync(string tableName, int limit, DateTime? upTo, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleGap>> GetGapsAsync(string tableName, TimeSpan interval, DateTime? expectedStart, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private async Task<(CandleFreshnessMonitor Monitor, CandleFreshnessHealthCheck Check)> BuildAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICandleRepository>(_repository);
        var series = await CandleSeriesTestSupport.LoadedProviderAsync(
            _time, TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H));
        var monitor = new CandleFreshnessMonitor(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), series, _time);
        return (monitor, new CandleFreshnessHealthCheck(monitor, _time));
    }

    private static Task<HealthCheckResult> Run(IHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    // ---- pure arithmetic ----

    [Theory]
    [InlineData(11, 5, 0)]   // 12:05 -> candle 11:00 (closed at 12:00) is expected, stored
    [InlineData(10, 5, 1)]   // 10:00 stored, 11:00 missing
    [InlineData(8, 5, 3)]
    [InlineData(10, 0, 0)]   // 12:00:00 is inside the grace window: 11:00 is not expected yet
    public void CandlesBehind_CountsMissingClosedCandles(int storedHour, int minute, int expected)
    {
        var now = Noon.AddMinutes(minute);

        Assert.Equal(expected, CandleFreshnessMonitor.CandlesBehind(Noon.Date.AddHours(storedHour), Hour, now));
    }

    [Fact]
    public async Task AnEmptyAnswerIsNotCachedSoSeriesThatAppearLaterAreMeasuredAtOnce()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICandleRepository>(_repository);
        var source = new FakeCandleSeriesSource();
        var series = CandleSeriesTestSupport.NewProvider(source, _time);
        var monitor = new CandleFreshnessMonitor(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), series, _time);
        _repository.Latest = Noon.AddHours(-1);

        Assert.Empty(await monitor.GetAsync(TestContext.Current.CancellationToken));

        source.Series.Add(TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H));
        await series.StartAsync(TestContext.Current.CancellationToken);
        await series.WaitForSeriesAsync(TestContext.Current.CancellationToken);

        // Well inside the 30 s cache window, yet the new series is already there.
        Assert.Single(await monitor.GetAsync(TestContext.Current.CancellationToken));
    }

    // ---- check ----

    [Fact]
    public async Task UpToDate_IsHealthy()
    {
        _repository.Latest = Noon.AddHours(-1);

        var result = await Run((await BuildAsync()).Check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task StaleAfterStartup_IsDegradedThenUnhealthy()
    {
        var (_, check) = await BuildAsync();
        _time.Advance(TimeSpan.FromMinutes(30));            // 12:35, past the startup grace
        _repository.Latest = Noon.AddHours(-2);             // one behind

        Assert.Equal(HealthStatus.Degraded, (await Run(check)).Status);

        _time.Advance(TimeSpan.FromHours(3));               // data did not move, three more candles closed
        var result = await Run(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("candles behind", result.Description);
    }

    [Fact]
    public async Task BackfillThatMoves_IsHealthy()
    {
        var (_, check) = await BuildAsync();
        _time.Advance(TimeSpan.FromMinutes(30));
        _repository.Latest = Noon.AddHours(-100);
        await Run(check);

        _time.Advance(TimeSpan.FromMinutes(1));
        _repository.Latest = Noon.AddHours(-50);            // still far behind, but moving
        var result = await Run(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Contains("catching up", result.Description);
    }

    [Fact]
    public async Task EmptyTable_IsDegradedAtStartupAndUnhealthyLater()
    {
        var (_, check) = await BuildAsync();

        Assert.Equal(HealthStatus.Degraded, (await Run(check)).Status);

        _time.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(HealthStatus.Unhealthy, (await Run(check)).Status);
    }

    [Fact]
    public async Task StaleDuringStartup_IsNeverWorseThanDegraded()
    {
        _repository.Latest = Noon.AddHours(-20);

        Assert.Equal(HealthStatus.Degraded, (await Run((await BuildAsync()).Check)).Status);
    }

    [Fact]
    public async Task DatabaseError_IsUnhealthy_WithoutLeakingTheMessage()
    {
        _repository.Throw = true;

        var result = await Run((await BuildAsync()).Check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.DoesNotContain("secret-host", result.Description);
    }

    [Fact]
    public async Task DatabaseAnswer_IsCachedBriefly()
    {
        var (_, check) = await BuildAsync();
        _repository.Latest = Noon.AddHours(-1);

        await Run(check);
        await Run(check);
        Assert.Equal(1, _repository.Calls);

        _time.Advance(TimeSpan.FromSeconds(31));
        await Run(check);
        Assert.Equal(2, _repository.Calls);
    }
}
