using BackgroundWorker.Health;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Health;

public class HealthCheckTests
{
    private const string Component = "test";
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    private readonly FakeTimeProvider _time = new();
    private readonly WorkerHealthState _state;

    public HealthCheckTests() => _state = new WorkerHealthState(_time);

    private static Task<HealthCheckResult> Run(IHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

    // ---- request/response APIs ----

    [Fact]
    public async Task RequestCheck_IsHealthyBeforeAnyRequest()
    {
        var result = await Run(new RequestHealthCheck(Component, _state, _time));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task RequestCheck_IsHealthyAfterSuccess()
    {
        _state.RecordSuccess(Component);

        var result = await Run(new RequestHealthCheck(Component, _state, _time));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Theory]
    [InlineData(1, HealthStatus.Degraded)]
    [InlineData(2, HealthStatus.Degraded)]
    [InlineData(3, HealthStatus.Unhealthy)]
    [InlineData(10, HealthStatus.Unhealthy)]
    public async Task RequestCheck_EscalatesWithConsecutiveFailures(int failures, HealthStatus expected)
    {
        for (var i = 0; i < failures; i++)
            _state.RecordFailure(Component, "timeout");

        var result = await Run(new RequestHealthCheck(Component, _state, _time));

        Assert.Equal(expected, result.Status);
        Assert.Equal("timeout", result.Data["lastError"]);
    }

    [Fact]
    public async Task RequestCheck_RecoversAfterASuccess()
    {
        for (var i = 0; i < 5; i++)
            _state.RecordFailure(Component, "down");
        _state.RecordSuccess(Component);

        var result = await Run(new RequestHealthCheck(Component, _state, _time));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    // ---- WebSocket ----

    private WebSocketHealthCheck Probe(TimeSpan? maxSilence, HealthStatus silenceStatus = HealthStatus.Unhealthy) =>
        new(Component, new WebSocketHealthOptions(Grace, Grace, maxSilence, silenceStatus), _state, _time);

    [Fact]
    public async Task WebSocket_IsDegradedWhileConnectingThenUnhealthyIfItNeverConnects()
    {
        var check = Probe(maxSilence: null);

        Assert.Equal(HealthStatus.Degraded, (await Run(check)).Status);

        _time.Advance(Grace + TimeSpan.FromSeconds(1));

        Assert.Equal(HealthStatus.Unhealthy, (await Run(check)).Status);
    }

    [Fact]
    public async Task WebSocket_IsHealthyWhenConnected()
    {
        _state.SetConnected(Component, true);

        Assert.Equal(HealthStatus.Healthy, (await Run(Probe(maxSilence: null))).Status);
    }

    [Fact]
    public async Task WebSocket_ShortDisconnectIsDegradedLongDisconnectIsUnhealthy()
    {
        _state.SetConnected(Component, true);
        _state.SetConnected(Component, false);
        var check = Probe(maxSilence: null);

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HealthStatus.Degraded, (await Run(check)).Status);

        _time.Advance(Grace);
        Assert.Equal(HealthStatus.Unhealthy, (await Run(check)).Status);
    }

    [Fact]
    public async Task WebSocket_RecoversWhenTheConnectionComesBack()
    {
        _state.SetConnected(Component, false);
        _time.Advance(Grace * 2);
        _state.SetConnected(Component, true);

        Assert.Equal(HealthStatus.Healthy, (await Run(Probe(maxSilence: null))).Status);
    }

    [Fact]
    public async Task WebSocket_BusyStreamGoingSilentIsReportedWithTheConfiguredStatus()
    {
        _state.SetConnected(Component, true);
        _state.RecordMessage(Component);
        var check = Probe(TimeSpan.FromSeconds(60));

        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(HealthStatus.Healthy, (await Run(check)).Status);

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(HealthStatus.Unhealthy, (await Run(check)).Status);

        _state.RecordMessage(Component);
        Assert.Equal(HealthStatus.Healthy, (await Run(check)).Status);
    }

    [Fact]
    public async Task WebSocket_QuietStreamOnlyDegrades()
    {
        // Liquidations are bursty: a long silence is worth noticing but is not an outage.
        _state.SetConnected(Component, true);
        var check = Probe(TimeSpan.FromMinutes(60), HealthStatus.Degraded);

        _time.Advance(TimeSpan.FromMinutes(59));
        Assert.Equal(HealthStatus.Healthy, (await Run(check)).Status);

        _time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HealthStatus.Degraded, (await Run(check)).Status);
    }

    [Fact]
    public async Task WebSocket_SilenceIsMeasuredFromTheConnectionWhenNothingArrivedYet()
    {
        _time.Advance(TimeSpan.FromMinutes(10));
        _state.SetConnected(Component, true);
        var check = Probe(TimeSpan.FromSeconds(60));

        _time.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(HealthStatus.Healthy, (await Run(check)).Status);
    }

    // ---- database ----

    [Fact]
    public async Task Database_UnreachableHostIsUnhealthyAndDoesNotLeakTheConnectionString()
    {
        const string secret = "s3cret-password";
        var check = new DatabaseHealthCheck($"Host=127.0.0.1;Port=1;Database=x;Username=u;Password={secret};Timeout=2");

        var result = await Run(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.DoesNotContain(secret, result.Description);
        Assert.DoesNotContain("127.0.0.1", result.Description);
    }
}
