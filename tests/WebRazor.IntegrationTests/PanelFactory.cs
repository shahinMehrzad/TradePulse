using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebRazor.Api;

namespace WebRazor.IntegrationTests;

/// <summary>
/// The real panel (its pages, tag helpers and layout) in memory, signed in as an administrator and talking to <see cref="StubApi"/> instead of
/// WebApi. The panel has no browser tests; these render the pages and read the HTML, which is where a link or a form that lost its id shows.
/// </summary>
public sealed class PanelFactory : WebApplicationFactory<Program>
{
    public StubApi Api { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not Development: that reads the user secrets of whoever runs the tests. Everything the panel needs is set here.
        builder.UseEnvironment("Testing");
        builder.UseSetting("Api:BaseUrl", "http://stub/");

        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultScheme = TestAuth.SchemeName;
                options.DefaultAuthenticateScheme = TestAuth.SchemeName;
                options.DefaultChallengeScheme = TestAuth.SchemeName;
            }).AddScheme<AuthenticationSchemeOptions, TestAuth>(TestAuth.SchemeName, _ => { });

            // What the pages ask of the API goes to the stub, without the handler that would look for the session's token.
            services.AddSingleton(new TradePulseApiClient(new HttpClient(Api) { BaseAddress = new Uri("http://stub/") }));
        });
    }

    public HttpClient SignedInClient() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
}

/// <summary>Everybody is the administrator "tester". The sign-in itself is the API's business and has its own tests.</summary>
internal sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "tester"), new Claim(ClaimTypes.Role, "Admin")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>
/// A made-up WebApi: answers by the start of the address and remembers what it was asked. A test sets the answer it needs with
/// <see cref="Answer"/>; <see cref="Reset"/> puts the usual ones back (call it at the start of every test: the factory is shared).
/// </summary>
public sealed class StubApi : HttpMessageHandler
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly object _gate = new();
    private readonly List<(string Prefix, string Json)> _answers = [];
    private readonly List<string> _requests = [];

    public StubApi() => Reset();

    /// <summary>The addresses (path and query) the pages asked for, oldest first.</summary>
    public IReadOnlyList<string> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _requests.Clear();
            _answers.Clear();
        }

        Answer("/api/lookups/exchanges", new[] { new { id = 1, name = "Binance" } });
        Answer("/api/lookups/markets", new[] { new { id = 1, name = "Spot" }, new { id = 2, name = "Futures" } });
        Answer("/api/lookups/symbols", new[] { new { id = 1, name = "BTC/USDT", description = "Bitcoin" }, new { id = 3, name = "ETH/USDT", description = "Ether" } });
        Answer("/api/lookups/time-frames", new[]
        {
            new { id = 900, name = "_15M", intervalToSeconds = 900, friendlyName = "15m" },
            new { id = 3600, name = "_1H", intervalToSeconds = 3600, friendlyName = "1h" },
        });
        Answer("/api/lookups/market-data-support", new[]
        {
            new { exchangeId = 1, marketId = 2, dataType = "OpenInterest", timeFrameIds = new[] { 900, 3600 } },
            new { exchangeId = 1, marketId = 2, dataType = "LongShortRatio", timeFrameIds = new[] { 900, 3600 } },
            new { exchangeId = 1, marketId = 2, dataType = "TakerVolume", timeFrameIds = new[] { 900, 3600 } },
        });
        Answer("/api/admin/configurations/candle-worker/late-starts", Array.Empty<object>());
        Series(
            Row(2, timeFrameId: 900, startDate: "2020-01-01T00:00:00", openInterest: true, statistics: false),
            Row(5, timeFrameId: 3600, startDate: "2021-06-01T00:00:00", openInterest: false, statistics: true));
    }

    /// <summary>Sets the answer to every address that starts with <paramref name="prefix"/> (a longer prefix set later wins).</summary>
    public void Answer(string prefix, object body)
    {
        lock (_gate)
        {
            _answers.RemoveAll(a => a.Prefix == prefix);
            _answers.Add((prefix, body as string ?? JsonSerializer.Serialize(body, Web)));
        }
    }

    public static object Row(int id, int timeFrameId, string startDate, bool openInterest, bool statistics, int symbolId = 1) => new
    {
        id, exchangeId = 1, marketId = 2, symbolId, timeFrameId, startDate, collectOpenInterest = openInterest, collectMarketStatistics = statistics,
    };

    /// <summary>The series list: these rows, and <paramref name="total"/> as the count of all that match (the rows shown when not given).</summary>
    public void Series(params object[] rows) => SeriesOf(rows.Length, rows);

    public void SeriesOf(int total, params object[] rows) =>
        Answer("/api/admin/configurations/candle-worker/odata", new { count = total, value = rows });

    public void LateStarts(params (int Id, string Start, string FirstCandle)[] late) =>
        Answer("/api/admin/configurations/candle-worker/late-starts", late.Select(l => new { id = l.Id, startDate = l.Start, firstCandle = l.FirstCandle }));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var address = request.RequestUri!.PathAndQuery;
        string? json;
        lock (_gate)
        {
            _requests.Add(address);
            json = _answers.Where(a => address.StartsWith(a.Prefix, StringComparison.OrdinalIgnoreCase)).OrderByDescending(a => a.Prefix.Length).Select(a => a.Json).FirstOrDefault();
        }

        var response = json is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        return Task.FromResult(response);
    }
}
