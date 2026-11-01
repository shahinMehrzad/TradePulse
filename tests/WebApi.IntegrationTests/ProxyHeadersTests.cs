using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.IntegrationTests;

/// <summary>
/// Behind the reverse proxy and the panel every request comes from one address. The login limit must still count the real visitors,
/// and a stranger must not be able to choose the address it counts.
/// </summary>
public class ProxyHeadersTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Makes the test server see every request as coming from <paramref name="remote"/>, as a real socket would.</summary>
    private sealed class RemoteAddressFilter(IPAddress remote) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) =>
            {
                context.Connection.RemoteIpAddress = remote;
                return continuation();
            });
            next(app);
        };
    }

    private HttpClient ClientFrom(string remote, int permitsPerMinute = 2)
    {
        var host = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:LoginPermitsPerMinute", permitsPerMinute.ToString());
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(IPAddress.Parse(remote))));
        });
        return host.CreateClient();
    }

    private static async Task<HttpStatusCode> LoginAsync(HttpClient client, string? forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { Email = "nobody@example.com", Username = "", Password = "wrong" })
        };
        if (forwardedFor is not null)
            request.Headers.Add("X-Forwarded-For", forwardedFor);
        return (await client.SendAsync(request, Ct)).StatusCode;
    }

    [Fact]
    public async Task BehindATrustedProxyEveryVisitorHasABudgetOfTheirOwn()
    {
        var proxy = ClientFrom("10.1.2.3"); // the panel or Caddy, on the stack's private network

        var visitorA = new[] { await LoginAsync(proxy, "198.51.100.1"), await LoginAsync(proxy, "198.51.100.1"), await LoginAsync(proxy, "198.51.100.1") };
        var visitorB = await LoginAsync(proxy, "198.51.100.2");

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], visitorA);
        Assert.Equal(HttpStatusCode.Unauthorized, visitorB); // not blocked because A used theirs up
    }

    [Fact]
    public async Task AStrangerOnTheInternetCannotChooseTheAddressThatIsCounted()
    {
        var stranger = ClientFrom("203.0.113.9"); // a public address: its X-Forwarded-For is not believed

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            statuses.Add(await LoginAsync(stranger, $"198.51.100.{10 + i}")); // a new "client" every time

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }

    [Fact]
    public async Task WithoutAForwardedHeaderTheCallersOwnAddressIsCounted()
    {
        var direct = ClientFrom("10.9.9.9");

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
            statuses.Add(await LoginAsync(direct, null));

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }
}
