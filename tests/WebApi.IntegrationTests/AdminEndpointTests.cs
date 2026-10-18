using Infrastructure.DbContext;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

public class AdminEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record AdminInfo(string UserName, string Email, string[] Roles);

    private static async Task<string> LoginAsync(HttpClient client, string email, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = email, Username = "", Password = password }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken;
    }

    private static Task<HttpResponseMessage> GetMeAsync(HttpClient client, string? bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/me");
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return client.SendAsync(request, Ct);
    }

    [Fact]
    public async Task WithoutATokenTheAdminEndpointIsUnauthorized()
    {
        var response = await GetMeAsync(api.CreateClient(), null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TheSeededAdminCanReadTheAdminEndpoint()
    {
        var client = api.CreateClient();
        var token = await LoginAsync(client, ApiFixture.AdminEmail, ApiFixture.AdminPassword);

        var response = await GetMeAsync(client, token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var info = await response.Content.ReadFromJsonAsync<AdminInfo>(Ct);
        Assert.Equal(ApiFixture.AdminEmail, info!.Email);
        Assert.Contains("Administrator", info.Roles);
    }

    [Fact]
    public async Task AnOrdinaryUserWithAValidTokenIsForbidden()
    {
        const string email = "ordinary@example.com";
        const string password = "OrdinaryPassword123!";
        using (var scope = api.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var created = await users.CreateAsync(new ApplicationUser { UserName = "ordinary", Email = email, EmailConfirmed = true }, password);
            Assert.True(created.Succeeded);
        }

        var client = api.CreateClient();
        var token = await LoginAsync(client, email, password);

        var response = await GetMeAsync(client, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LoginIsThrottledPerClient()
    {
        using var limited = api.WithWebHostBuilder(builder => builder.UseSetting("RateLimiting:LoginPermitsPerMinute", "2"));
        var client = limited.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/auth/login", new { Email = "nobody@example.com", Username = "", Password = "wrong" }, Ct);
            statuses.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests], statuses);
    }
}
