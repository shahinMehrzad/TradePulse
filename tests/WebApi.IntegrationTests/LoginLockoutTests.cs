using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Domain.Entities;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace WebApi.IntegrationTests;

/// <summary>
/// Wrong passwords and wrong codes, as a visitor meets them: an address that guesses is closed, and only that address; a code that is wrong
/// twice sends a message, five times locks the account; every round is longer than the one before it and none lasts for good; another
/// administrator can lift a lock. Each test has its own users, so no lock here touches the seeded administrator.
/// </summary>
public class LoginLockoutTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Home = "203.0.113.7";
    private const string Elsewhere = "198.51.100.9";
    private const string WrongPassword = "Definitely-Not-The-Password-1!";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Step(string Stage, string? ChallengeToken);

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

    /// <summary>A visitor from a public address (so what it says in X-Forwarded-For is not believed), and, when given, a clock the test moves.</summary>
    private HttpClient ClientFrom(string remote, FakeTimeProvider? clock = null)
    {
        var host = api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services => services.AddSingleton<IStartupFilter>(new RemoteAddressFilter(IPAddress.Parse(remote))));
            if (clock is not null)
                builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock));
        });
        return host.CreateClient();
    }

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string userName, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { Email = "", Username = userName, Password = password }, Ct);

    private static Task<HttpResponseMessage> CodeAsync(HttpClient client, string challenge, string code) =>
        client.PostAsJsonAsync("/api/auth/login/two-factor", new { ChallengeToken = challenge, Code = code, IsRecoveryCode = false }, Ct);

    private static async Task<string> ChallengeAsync(HttpClient client, string userName)
    {
        var response = await LoginAsync(client, userName, ApiFixture.NewUserPassword);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Step>(Ct))!.ChallengeToken!;
    }

    private static async Task WrongPasswordsAsync(HttpClient client, string userName, int count)
    {
        for (var i = 0; i < count; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, userName, WrongPassword)).StatusCode);
    }

    private static async Task<string> MessageAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("message").GetString()!;

    /// <summary>What the seeded administrator does to another account: signed in, and (unless told not to) just confirmed with a code.</summary>
    private async Task<HttpResponseMessage> AdminPostAsync(string url, bool confirmed = true)
    {
        var token = (await (await api.AdminLoginAsync(api.CreateClient(), Ct)).Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct))!.AccessToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (confirmed)
            request.Headers.Add("X-Elevation", await api.AdminElevationAsync(token, Ct));
        return await api.CreateClient().SendAsync(request, Ct);
    }

    private async Task<JsonElement> UserRowAsync(Guid id)
    {
        var token = (await (await api.AdminLoginAsync(api.CreateClient(), Ct)).Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct))!.AccessToken;
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var rows = await (await api.CreateClient().SendAsync(request, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        return rows.EnumerateArray().Single(row => row.GetProperty("id").GetGuid() == id);
    }

    private async Task<List<AuditLogEntry>> AuditAboutAsync(string userName)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ConfigDbContext>().AuditLog.AsNoTracking()
            .Where(entry => entry.Summary == userName && entry.UserName == "system").ToListAsync(Ct);
    }

    // ---- wrong passwords

    [Fact]
    public async Task FiveWrongPasswordsCloseThatAddressAndNobodyElse()
    {
        var account = await api.SetUpAdministratorAsync("pwlock", Ct);
        using var home = ClientFrom(Home);
        using var elsewhere = ClientFrom(Elsewhere);

        await WrongPasswordsAsync(home, account.UserName, 5);

        // The right password no longer gets in from the address that guessed, and the answer is the one a wrong password gets.
        var refused = await LoginAsync(home, account.UserName, ApiFixture.NewUserPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var wrong = await LoginAsync(elsewhere, account.UserName, WrongPassword);
        Assert.Equal(await MessageAsync(wrong), await MessageAsync(refused));

        // From any other address the owner signs in as always.
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(elsewhere, account.UserName, ApiFixture.NewUserPassword)).StatusCode);
    }

    [Fact]
    public async Task ARightPasswordInBetweenStartsTheCountOver()
    {
        var account = await api.SetUpAdministratorAsync("pwreset", Ct);
        using var client = ClientFrom(Home);

        await WrongPasswordsAsync(client, account.UserName, 4);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, account.UserName, ApiFixture.NewUserPassword)).StatusCode);
        await WrongPasswordsAsync(client, account.UserName, 4);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, account.UserName, ApiFixture.NewUserPassword)).StatusCode);
    }

    [Fact]
    public async Task ALockEndsByItselfAndTheNextOneIsLonger()
    {
        var account = await api.SetUpAdministratorAsync("pwround", Ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var client = ClientFrom(Home, clock);

        await WrongPasswordsAsync(client, account.UserName, 5);                                   // round 1: five minutes
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, account.UserName, ApiFixture.NewUserPassword)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(2));                                                   // it ended
        await WrongPasswordsAsync(client, account.UserName, 5);                                   // round 2: thirty minutes

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, account.UserName, ApiFixture.NewUserPassword)).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, account.UserName, ApiFixture.NewUserPassword)).StatusCode);
    }

    [Fact]
    public async Task AGuessFromManyAddressesClosesTheAccountForAWhileForEveryoneAndSendsAMessage()
    {
        var account = await api.SetUpAdministratorAsync("pwwide", Ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        for (var address = 0; address < 8; address++)
        {
            using var client = ClientFrom($"198.51.100.{address + 10}", clock);
            await WrongPasswordsAsync(client, account.UserName, address < 7 ? 4 : 2);              // 4 x 7 + 2 = 30
        }

        using var owner = ClientFrom("192.0.2.50", clock);                                         // an address that never guessed
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(owner, account.UserName, ApiFixture.NewUserPassword)).StatusCode);
        Assert.Contains(api.Alerts.About(account.UserName), alert => alert.Body.Contains("several addresses"));

        clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(owner, account.UserName, ApiFixture.NewUserPassword)).StatusCode);
    }

    [Fact]
    public async Task TheFirstLockSendsNoMessageButTheSecondRoundDoes()
    {
        var account = await api.SetUpAdministratorAsync("pwalert", Ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var client = ClientFrom(Home, clock);

        await WrongPasswordsAsync(client, account.UserName, 5);
        Assert.Empty(api.Alerts.About(account.UserName));
        Assert.Single(await AuditAboutAsync(account.UserName));                                     // written down all the same

        clock.Advance(TimeSpan.FromMinutes(6));
        await WrongPasswordsAsync(client, account.UserName, 5);

        var alert = Assert.Single(api.Alerts.About(account.UserName));
        Assert.Contains("round 2", alert.Body);
        Assert.Contains(Home, alert.Body);
        Assert.DoesNotContain(WrongPassword, alert.Title + alert.Body);
    }

    [Fact]
    public async Task APasswordOfAnAccountThatDoesNotExistCountsForNothingAndAnswersAsAlways()
    {
        using var client = ClientFrom(Home);

        for (var i = 0; i < 8; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, $"nobody{Guid.NewGuid():N}", WrongPassword)).StatusCode);
    }

    [Fact]
    public async Task WrongCurrentPasswordsOfASignedInUserAreCountedToo()
    {
        var account = await api.SetUpAdministratorAsync("pworacle", Ct);
        var token = (await (await api.LoginAsync(account, ApiFixture.NewUserPassword, Ct)).Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct))!.AccessToken;
        using var client = ClientFrom(Home);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/account/change-password", new { CurrentPassword = WrongPassword, NewPassword = "AnotherNewPassword1!" }, Ct);
        var withTheRightOne = await client.PostAsJsonAsync("/api/account/change-password",
            new { CurrentPassword = ApiFixture.NewUserPassword, NewPassword = "AnotherNewPassword1!" }, Ct);

        Assert.False(withTheRightOne.IsSuccessStatusCode);                                          // a stolen token is not a free password oracle
        using var elsewhere = ClientFrom(Elsewhere);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(elsewhere, account.UserName, ApiFixture.NewUserPassword)).StatusCode);
    }

    // ---- wrong codes

    [Fact]
    public async Task TheSecondWrongCodeSendsAMessageAndTheFifthLocksTheAccountForEveryAddress()
    {
        var account = await api.SetUpAdministratorAsync("codelock", Ct);
        using var home = ClientFrom(Home);
        var challenge = await ChallengeAsync(home, account.UserName);

        await CodeAsync(home, challenge, "000000");
        Assert.Empty(api.Alerts.About(account.UserName));                                           // one wrong code can be a slip

        await CodeAsync(home, challenge, "000001");
        var second = Assert.Single(api.Alerts.About(account.UserName));
        Assert.Contains(Home, second.Body);
        Assert.DoesNotMatch("000001", second.Title + second.Body);

        for (var i = 0; i < 3; i++)
            await CodeAsync(home, challenge, "000002");

        Assert.Equal(2, api.Alerts.About(account.UserName).Count);                                  // the lock is the second message
        Assert.Contains("locked", api.Alerts.About(account.UserName)[1].Title, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(await AuditAboutAsync(account.UserName), entry => entry.Details!.Contains("two-factor"));

        // Locked: the right code is refused here, and from any other address with a fresh sign-in too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await CodeAsync(home, challenge, Totp.Code(account.AuthenticatorKey))).StatusCode);
        using var elsewhere = ClientFrom(Elsewhere);
        var otherChallenge = await ChallengeAsync(elsewhere, account.UserName);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CodeAsync(elsewhere, otherChallenge, Totp.Code(account.AuthenticatorKey))).StatusCode);
    }

    [Fact]
    public async Task ARightCodeBeforeTheLockStartsTheCountOver()
    {
        var account = await api.SetUpAdministratorAsync("coderight", Ct);
        using var client = ClientFrom(Home);
        var challenge = await ChallengeAsync(client, account.UserName);

        for (var i = 0; i < 4; i++)
            await CodeAsync(client, challenge, "000000");
        Assert.Equal(HttpStatusCode.OK, (await CodeAsync(client, challenge, Totp.Code(account.AuthenticatorKey))).StatusCode);

        var again = await ChallengeAsync(client, account.UserName);
        for (var i = 0; i < 4; i++)
            await CodeAsync(client, again, "000000");
        Assert.Equal(HttpStatusCode.OK, (await CodeAsync(client, again, Totp.Code(account.AuthenticatorKey, DateTimeOffset.UtcNow.AddSeconds(30)))).StatusCode);
    }

    [Fact]
    public async Task ACodeLockEndsByItselfAndTheNextOneIsLonger()
    {
        var account = await api.SetUpAdministratorAsync("coderound", Ct);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var client = ClientFrom(Home, clock);

        var challenge = await ChallengeAsync(client, account.UserName);
        for (var i = 0; i < 5; i++)
            await CodeAsync(client, challenge, "000000");                                           // round 1: fifteen minutes
        clock.Advance(TimeSpan.FromMinutes(16));
        challenge = await ChallengeAsync(client, account.UserName);
        for (var i = 0; i < 5; i++)
            await CodeAsync(client, challenge, "000000");                                           // round 2: an hour

        // A new sign-in each time: the challenge of the password step lives half an hour. The code comes from the real clock, which Identity reads.
        clock.Advance(TimeSpan.FromMinutes(59));
        challenge = await ChallengeAsync(client, account.UserName);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CodeAsync(client, challenge, Totp.Code(account.AuthenticatorKey))).StatusCode);
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(HttpStatusCode.OK, (await CodeAsync(client, challenge, Totp.Code(account.AuthenticatorKey))).StatusCode);
    }

    // ---- lifting a lock

    [Fact]
    public async Task AnAdministratorSeesTheLockAndLiftsItWithAConfirmation()
    {
        var account = await api.SetUpAdministratorAsync("unlock", Ct);
        using var client = ClientFrom(Home);
        var challenge = await ChallengeAsync(client, account.UserName);
        for (var i = 0; i < 5; i++)
            await CodeAsync(client, challenge, "000000");
        await WrongPasswordsAsync(client, account.UserName, 5);

        var locked = await UserRowAsync(account.Id);
        Assert.NotEqual(JsonValueKind.Null, locked.GetProperty("lockedUntil").ValueKind);
        Assert.Equal(1, locked.GetProperty("lockedAddresses").GetInt32());
        Assert.False(locked.GetProperty("isDisabled").GetBoolean());                                // locked is not disabled

        var unconfirmed = await AdminPostAsync($"/api/admin/users/{account.Id}/unlock", confirmed: false);
        Assert.Equal(HttpStatusCode.Forbidden, unconfirmed.StatusCode);
        Assert.Contains("step_up_required", await unconfirmed.Content.ReadAsStringAsync(Ct));

        Assert.Equal(HttpStatusCode.NoContent, (await AdminPostAsync($"/api/admin/users/{account.Id}/unlock")).StatusCode);

        var open = await UserRowAsync(account.Id);
        Assert.Equal(JsonValueKind.Null, open.GetProperty("lockedUntil").ValueKind);
        Assert.Equal(0, open.GetProperty("lockedAddresses").GetInt32());
        var fresh = await ChallengeAsync(client, account.UserName);                                  // the address is open again too
        Assert.Equal(HttpStatusCode.OK, (await CodeAsync(client, fresh, Totp.Code(account.AuthenticatorKey))).StatusCode);
    }

    [Fact]
    public async Task UnlockingAUserThatDoesNotExistIsA404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await AdminPostAsync($"/api/admin/users/{Guid.NewGuid()}/unlock")).StatusCode);
    }

    [Fact]
    public async Task AResetOfThePasswordOrOfTwoFactorLiftsTheLocksToo()
    {
        var account = await api.SetUpAdministratorAsync("resetlock", Ct);
        using var client = ClientFrom(Home);
        await WrongPasswordsAsync(client, account.UserName, 5);
        Assert.Equal(1, (await UserRowAsync(account.Id)).GetProperty("lockedAddresses").GetInt32());

        Assert.True((await AdminPostAsync($"/api/admin/users/{account.Id}/reset-two-factor")).IsSuccessStatusCode);

        Assert.Equal(0, (await UserRowAsync(account.Id)).GetProperty("lockedAddresses").GetInt32());
    }
}
