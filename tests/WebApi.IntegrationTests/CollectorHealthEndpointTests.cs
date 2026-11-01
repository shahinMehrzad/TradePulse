using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>The relay of the collector's own /health. No collector runs next to these tests, so the address is left empty.</summary>
public class CollectorHealthEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Url = "/api/admin/collector/health";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Health(string State, string? Status, object[] Checks);

    [Fact]
    public async Task WithoutATokenItIsUnauthorized()
    {
        var response = await api.CreateClient().GetAsync(Url, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithoutAConfiguredAddressItSaysSoInsteadOfFailing()
    {
        var client = api.CreateClient();
        var login = await api.AdminLoginAsync(client, Ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await login.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken);

        var response = await client.GetAsync(Url, Ct);
        var health = await response.Content.ReadFromJsonAsync<Health>(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(("notConfigured", null, 0), (health!.State, health.Status, health.Checks.Length));
    }
}
