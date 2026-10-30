using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>A user's own account: change the password, make new recovery codes. Each test uses its own user.</summary>
public class AccountEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Info(string UserName, string Email, bool TwoFactorEnabled, int RecoveryCodesLeft);
    private record Codes(string[] RecoveryCodes);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? bearer, object? body = null, string? device = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (device is not null)
            request.Headers.Add("X-Device-Id", device);
        return await api.CreateClient().SendAsync(request, Ct);
    }

    private async Task<ApiFixture.Tokens> SignInAsync(ApiFixture.TestAccount account, string? device = null, string? password = null) =>
        (await (await api.LoginAsync(account, password ?? ApiFixture.NewUserPassword, Ct, device)).Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct))!;

    [Fact]
    public async Task AccountNeedsASignedInUser()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/account", null)).StatusCode);
    }

    [Fact]
    public async Task TheAccountShowsTheStateAndNoSecrets()
    {
        var account = await api.SetUpAdministratorAsync("info", Ct);
        var session = await SignInAsync(account);

        var response = await SendAsync(HttpMethod.Get, "/api/account", session.AccessToken);
        var body = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var info = System.Text.Json.JsonSerializer.Deserialize<Info>(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal(account.UserName, info.UserName);
        Assert.True(info.TwoFactorEnabled);
        Assert.Equal(10, info.RecoveryCodesLeft);
        Assert.DoesNotContain(account.AuthenticatorKey, body);
    }

    [Fact]
    public async Task ChangingThePasswordWorksOnlyWithTheRightCurrentOne()
    {
        var account = await api.SetUpAdministratorAsync("pwwrong", Ct);
        var session = await SignInAsync(account);

        var wrong = await SendAsync(HttpMethod.Post, "/api/account/change-password", session.AccessToken,
            new { CurrentPassword = "NotMyPassword123!", NewPassword = "AnotherNewPassword1!" });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.LoginAsync(account, ApiFixture.NewUserPassword, Ct)).StatusCode); // unchanged
    }

    [Fact]
    public async Task AWeakOrUnchangedPasswordIsRefused()
    {
        var account = await api.SetUpAdministratorAsync("pwweak", Ct);
        var session = await SignInAsync(account);

        var weak = await SendAsync(HttpMethod.Post, "/api/account/change-password", session.AccessToken,
            new { CurrentPassword = ApiFixture.NewUserPassword, NewPassword = "short1A!" });
        var same = await SendAsync(HttpMethod.Post, "/api/account/change-password", session.AccessToken,
            new { CurrentPassword = ApiFixture.NewUserPassword, NewPassword = ApiFixture.NewUserPassword });

        Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, same.StatusCode);
    }

    [Fact]
    public async Task ChangingThePasswordKeepsThisDeviceAndEndsTheOthers()
    {
        var account = await api.SetUpAdministratorAsync("pwdevices", Ct);
        var laptop = await SignInAsync(account, device: "laptop");
        var phone = await SignInAsync(account, device: "phone");
        const string next = "ASecondNewPassword2!";

        var changed = await SendAsync(HttpMethod.Post, "/api/account/change-password", laptop.AccessToken,
            new { CurrentPassword = ApiFixture.NewUserPassword, NewPassword = next }, device: "laptop");

        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SendAsync(HttpMethod.Post, "/api/auth/refresh-token", null, new { laptop.RefreshToken }, "laptop")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Post, "/api/auth/refresh-token", null, new { phone.RefreshToken }, "phone")).StatusCode);
        // The old password is gone, the new one signs in (the code step only follows a right password).
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/login",
            new { Email = "", Username = account.UserName, Password = ApiFixture.NewUserPassword }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.LoginAsync(account, next, Ct)).StatusCode);
    }

    [Fact]
    public async Task NewRecoveryCodesReplaceTheOldOnesAndNeedThePassword()
    {
        var account = await api.SetUpAdministratorAsync("codes", Ct);
        var session = await SignInAsync(account);

        var refused = await SendAsync(HttpMethod.Post, "/api/account/recovery-codes", session.AccessToken, new { Password = "NotMyPassword123!" });
        var made = await SendAsync(HttpMethod.Post, "/api/account/recovery-codes", session.AccessToken, new { Password = ApiFixture.NewUserPassword });
        var fresh = (await made.Content.ReadFromJsonAsync<Codes>(Ct))!.RecoveryCodes;

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal(10, fresh.Length);
        Assert.Empty(fresh.Intersect(account.RecoveryCodes));

        async Task<HttpStatusCode> RecoveryLoginAsync(string code)
        {
            using var client = api.CreateClient();
            var login = await client.PostAsJsonAsync("/api/auth/login", new { Email = "", Username = account.UserName, Password = ApiFixture.NewUserPassword }, Ct);
            var challenge = (await login.Content.ReadFromJsonAsync<Step>(Ct))!.ChallengeToken;
            return (await client.PostAsJsonAsync("/api/auth/login/two-factor", new { ChallengeToken = challenge, Code = code, IsRecoveryCode = true }, Ct)).StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await RecoveryLoginAsync(account.RecoveryCodes[0]));
        Assert.Equal(HttpStatusCode.OK, await RecoveryLoginAsync(fresh[0]));
    }

    [Fact]
    public async Task AChallengeTokenOpensNothingHere()
    {
        var name = "challenge" + Guid.NewGuid().ToString("N")[..8];
        var admin = (await (await api.AdminLoginAsync(api.CreateClient(), Ct)).Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct))!.AccessToken;
        var created = await (await SendAsync(HttpMethod.Post, "/api/admin/users", admin, new { UserName = name, Email = $"{name}@example.com" })).Content.ReadFromJsonAsync<Created>(Ct);
        var login = await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { Email = "", Username = name, Password = created!.TemporaryPassword }, Ct);
        var challenge = (await login.Content.ReadFromJsonAsync<Step>(Ct))!.ChallengeToken!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(HttpMethod.Get, "/api/account", challenge)).StatusCode);
    }

    private record Step(string ChallengeToken);
    private record Created(Guid UserId, string TemporaryPassword);
}
