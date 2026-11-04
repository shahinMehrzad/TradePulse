using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Application.Interfaces;
using Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WebApi.IntegrationTests;

/// <summary>
/// A two-factor code works once. Identity accepts one for a minute or two and lets it be used again in that time, so a code seen over a
/// shoulder would open the account as well. These tests put the real guard back (the other tests have one that lets everything through).
/// </summary>
public class ReplayGuardTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Step(string Stage, string? ChallengeToken, string? AccessToken);

    private HttpClient RealGuardClient() => api.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
    {
        services.RemoveAll<ITwoFactorReplayGuard>();
        services.AddScoped<ITwoFactorReplayGuard, TwoFactorReplayGuard>();
    })).CreateClient();

    private static async Task<string> ChallengeAsync(HttpClient client, string userName)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { Email = "", Username = userName, Password = ApiFixture.NewUserPassword }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Step>(Ct))!.ChallengeToken!;
    }

    private static Task<HttpResponseMessage> CodeAsync(HttpClient client, string challenge, string code) =>
        client.PostAsJsonAsync("/api/auth/login/two-factor", new { ChallengeToken = challenge, Code = code, IsRecoveryCode = false }, Ct);

    private static async Task<string> MessageAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("message").GetString()!;

    /// <summary>The code of the step after this one: Identity accepts it too, and it is a different number.</summary>
    private static string NextCode(string key) => Totp.Code(key, DateTimeOffset.UtcNow.AddSeconds(30));

    [Fact]
    public async Task ACodeThatSignedYouInDoesNotSignInASecondTime()
    {
        var account = await api.SetUpAdministratorAsync("replay", Ct);
        using var client = RealGuardClient();
        var code = Totp.Code(account.AuthenticatorKey);

        Assert.Equal(HttpStatusCode.OK, (await CodeAsync(client, await ChallengeAsync(client, account.UserName), code)).StatusCode);

        var again = await CodeAsync(client, await ChallengeAsync(client, account.UserName), code);
        Assert.Equal(HttpStatusCode.Unauthorized, again.StatusCode);
        Assert.Contains("already used", await MessageAsync(again));

        // The next code is a different one, and it works.
        Assert.Equal(HttpStatusCode.OK, (await CodeAsync(client, await ChallengeAsync(client, account.UserName), NextCode(account.AuthenticatorKey))).StatusCode);
    }

    [Fact]
    public async Task AReusedCodeIsNotAWrongGuessSoItNeitherLocksNorSendsAMessage()
    {
        var account = await api.SetUpAdministratorAsync("replaycount", Ct);
        using var client = RealGuardClient();
        var code = Totp.Code(account.AuthenticatorKey);
        await CodeAsync(client, await ChallengeAsync(client, account.UserName), code);

        for (var i = 0; i < 7; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await CodeAsync(client, await ChallengeAsync(client, account.UserName), code)).StatusCode);

        // Seven refusals of a code that was right: not a lock, and not an alarm. The account still signs in with its next code.
        Assert.Empty(api.Alerts.About(account.UserName));
        Assert.Equal(HttpStatusCode.OK, (await CodeAsync(client, await ChallengeAsync(client, account.UserName), NextCode(account.AuthenticatorKey))).StatusCode);
    }

    [Fact]
    public async Task ACodeIsNotAcceptedAtTheStepUpEitherAfterItWasUsedToSignIn()
    {
        var account = await api.SetUpAdministratorAsync("replaystep", Ct);
        using var client = RealGuardClient();
        var code = Totp.Code(account.AuthenticatorKey);
        var signedIn = await (await CodeAsync(client, await ChallengeAsync(client, account.UserName), code)).Content.ReadFromJsonAsync<Step>(Ct);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signedIn!.AccessToken);

        var same = await client.PostAsJsonAsync("/api/account/step-up", new { Code = code }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
        Assert.Contains("already used", await same.Content.ReadAsStringAsync(Ct));

        var next = await client.PostAsJsonAsync("/api/account/step-up", new { Code = NextCode(account.AuthenticatorKey) }, Ct);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task AWrongCodeIsStillAWrongCodeAndCounts()
    {
        var account = await api.SetUpAdministratorAsync("replaywrong", Ct);
        using var client = RealGuardClient();
        var challenge = await ChallengeAsync(client, account.UserName);

        await CodeAsync(client, challenge, "000000");
        await CodeAsync(client, challenge, "000001");

        Assert.Single(api.Alerts.About(account.UserName));   // the second wrong code in a row, as before
    }
}
