using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>
/// The whole way into the panel: temporary password, forced change, forced two-factor, then the code at every sign-in,
/// and what an administrator can do to the accounts of the others. Each test creates its own users, so a lockout
/// here never touches the seeded administrator the other test classes sign in with.
/// </summary>
public class SignInFlowTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Password = "BrandNewPassword789!";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Step(string Stage, string? AccessToken, string? RefreshToken, string? ChallengeToken, bool MustChangePassword, bool NeedsTwoFactorSetup, string[]? RecoveryCodes);
    private record Temporary(Guid UserId, string UserName, string TemporaryPassword);
    private record Enrollment(string SharedKey);
    private record UserRow(Guid Id, string UserName, string Email, bool TwoFactorEnabled, bool MustChangePassword, bool IsDisabled);

    private record Account(string UserName, string Email, Guid Id, string AuthenticatorKey, string[] RecoveryCodes);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Ct))!;

    private Task<HttpResponseMessage> PostAsync(string url, object? body, string? bearer = null, string? device = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (device is not null)
            request.Headers.Add("X-Device-Id", device);
        return api.CreateClient().SendAsync(request, Ct);
    }

    private async Task<string> AdminTokenAsync() =>
        (await ReadAsync<Step>(await api.AdminLoginAsync(api.CreateClient(), Ct))).AccessToken!;

    private async Task<Temporary> CreateAdministratorAsync(string name)
    {
        var response = await PostAsync("/api/admin/users", new { UserName = name, Email = $"{name}@example.com" }, await AdminTokenAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<Temporary>(response);
    }

    private static string Unique(string prefix) => prefix + Guid.NewGuid().ToString("N")[..8];

    private Task<HttpResponseMessage> LoginAsync(string userName, string password, string? device = null) =>
        PostAsync("/api/auth/login", new { Email = "", Username = userName, Password = password }, device: device);

    /// <summary>Creates an administrator and takes them through the setup, as they would on the first sign-in.</summary>
    private async Task<Account> SetUpAdministratorAsync(string prefix)
    {
        var name = Unique(prefix);
        var created = await CreateAdministratorAsync(name);

        var challenge = (await ReadAsync<Step>(await LoginAsync(name, created.TemporaryPassword))).ChallengeToken;
        challenge = (await ReadAsync<Step>(await PostAsync("/api/auth/setup/password",
            new { ChallengeToken = challenge, CurrentPassword = created.TemporaryPassword, NewPassword = Password }))).ChallengeToken;
        var key = (await ReadAsync<Enrollment>(await PostAsync("/api/auth/setup/two-factor/begin", new { ChallengeToken = challenge }))).SharedKey;
        var done = await ReadAsync<Step>(await PostAsync("/api/auth/setup/two-factor/confirm", new { ChallengeToken = challenge, Code = Totp.Code(key) }));

        return new Account(name, $"{name}@example.com", created.UserId, key, done.RecoveryCodes!);
    }

    private async Task<HttpResponseMessage> FullLoginAsync(Account account, string? device = null)
    {
        var step = await ReadAsync<Step>(await LoginAsync(account.UserName, Password, device));
        return await PostAsync("/api/auth/login/two-factor",
            new { step.ChallengeToken, Code = Totp.Code(account.AuthenticatorKey), IsRecoveryCode = false }, device: device);
    }

    private async Task<HttpStatusCode> MeAsync(string bearer)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return (await api.CreateClient().SendAsync(request, Ct)).StatusCode;
    }

    [Fact]
    public async Task ANewAdministratorGetsNoTokenFromTheTemporaryPassword()
    {
        var created = await CreateAdministratorAsync(Unique("fresh"));

        var step = await ReadAsync<Step>(await LoginAsync(created.UserName, created.TemporaryPassword));

        Assert.Equal("SetupRequired", step.Stage);
        Assert.True(step.MustChangePassword);
        Assert.True(step.NeedsTwoFactorSetup);
        Assert.Null(step.AccessToken);
        Assert.Null(step.RefreshToken);
        Assert.NotNull(step.ChallengeToken);
    }

    [Fact]
    public async Task AChallengeTokenIsNotAnAccessToken()
    {
        var created = await CreateAdministratorAsync(Unique("challenge"));
        var step = await ReadAsync<Step>(await LoginAsync(created.UserName, created.TemporaryPassword));

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(step.ChallengeToken!));
    }

    [Fact]
    public async Task TwoFactorCannotBeEnrolledBeforeThePasswordIsChanged()
    {
        var created = await CreateAdministratorAsync(Unique("order"));
        var step = await ReadAsync<Step>(await LoginAsync(created.UserName, created.TemporaryPassword));

        var response = await PostAsync("/api/auth/setup/two-factor/begin", new { step.ChallengeToken });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AWeakNewPasswordIsRefusedAndNothingChanges()
    {
        var created = await CreateAdministratorAsync(Unique("weak"));
        var step = await ReadAsync<Step>(await LoginAsync(created.UserName, created.TemporaryPassword));

        var response = await PostAsync("/api/auth/setup/password",
            new { step.ChallengeToken, CurrentPassword = created.TemporaryPassword, NewPassword = "short1A!" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("SetupRequired", (await ReadAsync<Step>(await LoginAsync(created.UserName, created.TemporaryPassword))).Stage);
    }

    [Fact]
    public async Task FinishingTheSetupSignsInAndShowsTheRecoveryCodesOnce()
    {
        var account = await SetUpAdministratorAsync("setup");

        Assert.Equal(10, account.RecoveryCodes.Length);
        var step = await ReadAsync<Step>(await LoginAsync(account.UserName, Password));
        Assert.Equal("TwoFactorRequired", step.Stage); // the next sign-in asks for the code
    }

    [Fact]
    public async Task ASignInNeedsTheCodeAndThenWorksWithTheTokens()
    {
        var account = await SetUpAdministratorAsync("code");

        var done = await ReadAsync<Step>(await FullLoginAsync(account));

        Assert.Equal("Authenticated", done.Stage);
        Assert.Equal(HttpStatusCode.OK, await MeAsync(done.AccessToken!));
    }

    [Fact]
    public async Task AWrongCodeIsRefusedAndALockoutFollowsRepeatedOnes()
    {
        var account = await SetUpAdministratorAsync("lockout");
        var step = await ReadAsync<Step>(await LoginAsync(account.UserName, Password));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
            statuses.Add((await PostAsync("/api/auth/login/two-factor", new { step.ChallengeToken, Code = "000000", IsRecoveryCode = false })).StatusCode);
        var right = await PostAsync("/api/auth/login/two-factor",
            new { step.ChallengeToken, Code = Totp.Code(account.AuthenticatorKey), IsRecoveryCode = false });

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.Equal(HttpStatusCode.Unauthorized, right.StatusCode); // locked: even the right code is refused now
    }

    [Fact]
    public async Task ARecoveryCodeSignsInOnce()
    {
        var account = await SetUpAdministratorAsync("recovery");
        var code = account.RecoveryCodes[0];

        async Task<HttpStatusCode> TryAsync()
        {
            var step = await ReadAsync<Step>(await LoginAsync(account.UserName, Password));
            return (await PostAsync("/api/auth/login/two-factor", new { step.ChallengeToken, Code = code, IsRecoveryCode = true })).StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, await TryAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, await TryAsync());
    }

    [Fact]
    public async Task ResettingAPasswordEndsTheSessionsAndForcesANewChange()
    {
        var account = await SetUpAdministratorAsync("resetpw");
        var session = await ReadAsync<Step>(await FullLoginAsync(account, device: "laptop"));

        var reset = await PostAsync($"/api/admin/users/{account.Id}/reset-password", null, await AdminTokenAsync());
        var temporary = await ReadAsync<Temporary>(reset);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("/api/auth/refresh-token", new { session.RefreshToken }, device: "laptop")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(account.UserName, Password)).StatusCode);
        var step = await ReadAsync<Step>(await LoginAsync(account.UserName, temporary.TemporaryPassword));
        Assert.Equal("SetupRequired", step.Stage);
        Assert.True(step.MustChangePassword);
        Assert.False(step.NeedsTwoFactorSetup); // the authenticator stays; only the password is new
    }

    [Fact]
    public async Task ResettingTwoFactorSendsTheUserBackToTheSetup()
    {
        var account = await SetUpAdministratorAsync("reset2fa");

        var reset = await PostAsync($"/api/admin/users/{account.Id}/reset-two-factor", null, await AdminTokenAsync());

        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        var step = await ReadAsync<Step>(await LoginAsync(account.UserName, Password));
        Assert.Equal("SetupRequired", step.Stage);
        Assert.True(step.NeedsTwoFactorSetup);
        Assert.False(step.MustChangePassword);
    }

    [Fact]
    public async Task ADisabledUserCannotSignInOrRefreshAndCanBeEnabledAgain()
    {
        var account = await SetUpAdministratorAsync("disabled");
        var session = await ReadAsync<Step>(await FullLoginAsync(account, device: "phone"));
        var admin = await AdminTokenAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync($"/api/admin/users/{account.Id}/disable", null, admin)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(account.UserName, Password)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostAsync("/api/auth/refresh-token", new { session.RefreshToken }, device: "phone")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync($"/api/admin/users/{account.Id}/enable", null, admin)).StatusCode);
        Assert.Equal("Authenticated", (await ReadAsync<Step>(await FullLoginAsync(account))).Stage);
    }

    [Fact]
    public async Task NobodyCanDisableOrResetTheirOwnAccount()
    {
        var account = await SetUpAdministratorAsync("self");
        var token = (await ReadAsync<Step>(await FullLoginAsync(account))).AccessToken!;

        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync($"/api/admin/users/{account.Id}/disable", null, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync($"/api/admin/users/{account.Id}/reset-two-factor", null, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostAsync($"/api/admin/users/{account.Id}/reset-password", null, token)).StatusCode);
    }

    [Fact]
    public async Task TheUserListShowsTheStateOfEachAccountButNoSecrets()
    {
        var account = await SetUpAdministratorAsync("listed");
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/admin/users");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AdminTokenAsync());

        var response = await api.CreateClient().SendAsync(request, Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        var users = System.Text.Json.JsonSerializer.Deserialize<UserRow[]>(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;

        var row = Assert.Single(users, u => u.Id == account.Id);
        Assert.True(row.TwoFactorEnabled);
        Assert.False(row.MustChangePassword);
        Assert.False(row.IsDisabled);
        Assert.DoesNotContain(account.AuthenticatorKey, body);
    }

    [Fact]
    public async Task AUserNameThatIsTakenIsRefused()
    {
        var account = await SetUpAdministratorAsync("taken");

        var response = await PostAsync("/api/admin/users", new { account.UserName, Email = "other@example.com" }, await AdminTokenAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
