using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Infrastructure.Persistence;

namespace WebApi.IntegrationTests;

/// <summary>
/// An access token is a signature and an expiry. These tests are about what happens to one that is still valid when the user it was made for
/// is disabled, reset or loses a role: it stops working at once (here the host asks the database at every request), and not when it runs out.
/// </summary>
public class TokenRevocationTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string AccountUrl = "/api/account";
    private const string AdminMeUrl = "/api/admin/me";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Session(string AccessToken, DateTime ExpiresAt);

    private static async Task<HttpStatusCode> GetAsync(HttpClient client, string url, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await client.SendAsync(request, Ct)).StatusCode;
    }

    /// <summary>Signs a new administrator in (password and code) and returns the access token.</summary>
    private async Task<(ApiFixture.TestAccount Account, string Token)> SignedInAsync(string prefix)
    {
        var account = await api.SetUpAdministratorAsync(prefix, Ct);
        var tokens = await (await api.LoginAsync(account, ApiFixture.NewUserPassword, Ct)).Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct);
        return (account, tokens!.AccessToken);
    }

    /// <summary>What the seeded administrator does to another account: signed in, and just confirmed with a code.</summary>
    private async Task AdminPostAsync(string url)
    {
        var token = (await (await api.AdminLoginAsync(api.CreateClient(), Ct)).Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct))!.AccessToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-Elevation", await api.AdminElevationAsync(token, Ct));
        var response = await api.CreateClient().SendAsync(request, Ct);
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode}");
    }

    [Fact]
    public async Task ADisabledUsersTokenStopsWorkingAtOnce()
    {
        var (account, token) = await SignedInAsync("disabled");
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, AccountUrl, token));

        await AdminPostAsync($"/api/admin/users/{account.Id}/disable");

        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(client, AccountUrl, token));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(client, AdminMeUrl, token));
    }

    [Fact]
    public async Task AnotherUsersTokenIsNotTouched()
    {
        var (disabled, _) = await SignedInAsync("victim");
        var (_, other) = await SignedInAsync("bystander");
        using var client = api.CreateClient();

        await AdminPostAsync($"/api/admin/users/{disabled.Id}/disable");

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, AccountUrl, other));
    }

    [Fact]
    public async Task AResetPasswordEndsTheOldTokens()
    {
        var (account, token) = await SignedInAsync("pwreset");
        using var client = api.CreateClient();

        await AdminPostAsync($"/api/admin/users/{account.Id}/reset-password");

        // The account has to set up again before it gets a new session; a token from before the reset is not that.
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(client, AccountUrl, token));
    }

    [Fact]
    public async Task AResetOfTwoFactorEndsTheOldTokens()
    {
        var (account, token) = await SignedInAsync("tfreset");
        using var client = api.CreateClient();

        await AdminPostAsync($"/api/admin/users/{account.Id}/reset-two-factor");

        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(client, AccountUrl, token));
    }

    [Fact]
    public async Task ARemovedAdministratorRoleEndsTheAdminAccessOfTheToken()
    {
        var (account, token) = await SignedInAsync("demoted");
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, AdminMeUrl, token));

        using (var scope = api.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(account.Id.ToString()))!;
            var role = (await users.GetRolesAsync(user)).Single();
            Assert.True((await users.RemoveFromRoleAsync(user, role)).Succeeded);
        }

        // The token still says Administrator; the account does not any more.
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(client, AdminMeUrl, token));
    }

    [Fact]
    public async Task AnEndpointThatLooksTheAccountUpAnswers401NotAServerErrorInsideTheKeepingTime()
    {
        var (account, token) = await SignedInAsync("insidewindow");
        using var cached = api.WithWebHostBuilder(builder => builder.UseSetting("TokenRevocation:CacheSeconds", "30"));
        using var client = cached.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, AccountUrl, token));

        await AdminPostAsync($"/api/admin/users/{account.Id}/disable");

        // The token is still believed (kept for 30 seconds), but this endpoint reads the account itself and finds it disabled.
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(client, AccountUrl, token));
    }

    [Fact]
    public async Task AccessTokensLiveAFewMinutes()
    {
        var account = await api.SetUpAdministratorAsync("short", Ct);

        var session = (await (await api.LoginAsync(account, ApiFixture.NewUserPassword, Ct)).Content.ReadFromJsonAsync<Session>(Ct))!;

        var life = session.ExpiresAt.ToUniversalTime() - DateTime.UtcNow;
        Assert.InRange(life, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5.5));
    }

    [Fact]
    public async Task WhatIsKnownAboutAUserIsKeptForTheConfiguredSecondsAndNoLonger()
    {
        var (account, token) = await SignedInAsync("cached");
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cached = api.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("TokenRevocation:CacheSeconds", "10");
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
        });
        using var client = cached.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, ApiFixture.CandleUrl, token));   // asks the database, and remembers

        await AdminPostAsync($"/api/admin/users/{account.Id}/disable");

        Assert.Equal(HttpStatusCode.OK, await GetAsync(client, ApiFixture.CandleUrl, token));           // still inside the ten seconds
        clock.Advance(TimeSpan.FromSeconds(11));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(client, ApiFixture.CandleUrl, token)); // asked again
    }
}
