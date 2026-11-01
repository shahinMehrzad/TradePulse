using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Controllers.Admin;

namespace WebApi.IntegrationTests;

/// <summary>
/// What every admin controller gets for free by inheriting <see cref="AdminControllerBase"/>: the admin policy and the
/// default route <c>api/admin/[controller]</c>, which a controller may replace with its own <c>[Route]</c>.
/// </summary>
public class AdminControllerBaseTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);

    private HttpClient Client(string? bearer = null)
    {
        // The probe controllers below live in this test assembly, so this host has to be told to load them.
        var client = api
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                services.AddControllers().AddApplicationPart(typeof(AdminControllerBaseTests).Assembly)))
            .CreateClient();

        if (bearer is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }

    private async Task<string> AdminTokenAsync()
    {
        var response = await api.AdminLoginAsync(api.CreateClient(), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken;
    }

    private async Task<HttpStatusCode> StatusAsync(string url, string? bearer)
    {
        using var client = Client(bearer);
        return (await client.GetAsync(url, Ct)).StatusCode;
    }

    [Fact]
    public async Task AControllerWithoutItsOwnRouteGetsTheAdminPrefix()
    {
        var token = await AdminTokenAsync();

        Assert.Equal(HttpStatusCode.OK, await StatusAsync("/api/admin/defaultroutedprobe", token));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync("/api/defaultroutedprobe", token));
    }

    [Fact]
    public async Task AControllerCanReplaceTheRouteWithItsOwn()
    {
        var token = await AdminTokenAsync();

        Assert.Equal(HttpStatusCode.OK, await StatusAsync("/api/custom-probe", token));
        // The inherited default must not stay reachable next to the replacement.
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync("/api/admin/customroutedprobe", token));
    }

    [Theory]
    [InlineData("/api/admin/defaultroutedprobe")]
    [InlineData("/api/custom-probe")]
    public async Task EveryInheritingControllerIsAdminOnly(string url)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusAsync(url, bearer: null));
    }
}

public class DefaultRoutedProbeController : AdminControllerBase
{
    [HttpGet]
    public string Get() => "ok";
}

[Route("api/custom-probe")]
public class CustomRoutedProbeController : AdminControllerBase
{
    [HttpGet]
    public string Get() => "ok";
}
