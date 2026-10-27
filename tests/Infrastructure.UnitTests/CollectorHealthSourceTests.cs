using System.Net;
using System.Text;
using Application.Dtos.Collector;
using Infrastructure.Monitoring;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Infrastructure.UnitTests;

public class CollectorHealthSourceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Url = "http://collector.internal:5080/health";

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Requested { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Requested = request.RequestUri;
            return Task.FromResult(respond());
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    private CollectorHealthSource Source(StubHandler handler, string? url = Url) => new(
        new HttpClient(handler),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [CollectorHealthSource.UrlSetting] = url }).Build(),
        _time,
        NullLogger<CollectorHealthSource>.Instance);

    private const string Healthy = """
        { "status": "Degraded", "totalDurationMs": 4, "checks": [
            { "name": "database", "status": "Healthy", "description": "Connected", "data": { } },
            { "name": "candle-freshness", "status": "Degraded", "description": "1 series behind", "data": { "behind": 1, "worst": "binance_futures_btcusdt_1h" } } ] }
        """;

    [Fact]
    public async Task TheAnswerOfTheCollectorIsRelayedCheckByCheck()
    {
        var handler = new StubHandler(() => Json(HttpStatusCode.OK, Healthy));

        var result = await Source(handler).GetAsync(Ct);

        Assert.Equal((CollectorHealthState.Answered, "Degraded"), (result.State, result.Status));
        Assert.Equal(new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc), result.CheckedAt);
        Assert.Equal(["database", "candle-freshness"], result.Checks.Select(c => c.Name));
        Assert.Equal(("Degraded", "1 series behind"), (result.Checks[1].Status, result.Checks[1].Description));
        Assert.Equal("1", result.Checks[1].Data["behind"]);                              // numbers become text
        Assert.Equal("binance_futures_btcusdt_1h", result.Checks[1].Data["worst"]);
        Assert.Equal(new Uri(Url), handler.Requested);
    }

    [Fact]
    public async Task AnUnhealthyCollectorAnswersWith503AndThatIsStillAnAnswer()
    {
        var body = """{ "status": "Unhealthy", "checks": [ { "name": "database", "status": "Unhealthy", "description": "Cannot connect", "data": {} } ] }""";

        var result = await Source(new StubHandler(() => Json(HttpStatusCode.ServiceUnavailable, body))).GetAsync(Ct);

        Assert.Equal((CollectorHealthState.Answered, "Unhealthy"), (result.State, result.Status));
        Assert.Equal("Cannot connect", Assert.Single(result.Checks).Description);
    }

    [Fact]
    public async Task WithoutAnAddressTheCollectorIsNotAskedAtAll()
    {
        var handler = new StubHandler(() => Json(HttpStatusCode.OK, Healthy));

        var result = await Source(handler, url: "  ").GetAsync(Ct);

        Assert.Equal((CollectorHealthState.NotConfigured, null, 0), (result.State, result.Status, handler.Calls));
        Assert.Empty(result.Checks);
    }

    [Fact]
    public async Task ACollectorThatIsDownIsUnreachableAndTheMessageStaysInTheLog()
    {
        var source = Source(new StubHandler(() => throw new HttpRequestException("No connection could be made to secret-host:5080")));

        var result = await source.GetAsync(Ct);

        Assert.Equal((CollectorHealthState.Unreachable, null), (result.State, result.Status));
        Assert.Empty(result.Checks);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{ "status": "Healthy" }""")]                                  // no checks
    [InlineData("""{ "checks": [ { "name": "x" } ] }""")]                       // a check without a status
    [InlineData("<html>a login page of something else</html>")]
    public async Task AnAnswerThatIsNotTheHealthReportIsUnreachable(string body)
    {
        var result = await Source(new StubHandler(() => Json(HttpStatusCode.OK, body))).GetAsync(Ct);

        Assert.Equal(CollectorHealthState.Unreachable, result.State);
    }

    [Fact]
    public async Task ShuttingDownIsNotReportedAsUnreachable()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var source = Source(new StubHandler(() => throw new TaskCanceledException()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetAsync(cancelled.Token));
    }
}
