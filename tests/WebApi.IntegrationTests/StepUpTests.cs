using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>Sensitive actions ask for a second-factor code just entered, on top of the session: a stolen access token alone is not enough.</summary>
public class StepUpTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Elevation(string Token, DateTime ExpiresAt);
    private record Created(Guid UserId, string TemporaryPassword);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string? bearer, object? body = null, string? elevation = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        if (elevation is not null)
            request.Headers.Add("X-Elevation", elevation);
        return await api.CreateClient().SendAsync(request, Ct);
    }

    private async Task<string> AccessTokenAsync(ApiFixture.TestAccount account) =>
        (await (await api.LoginAsync(account, ApiFixture.NewUserPassword, Ct)).Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct))!.AccessToken;

    private async Task<string> AdminTokenAsync() =>
        (await (await api.AdminLoginAsync(api.CreateClient(), Ct)).Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct))!.AccessToken;

    private static object NewUser() => new { UserName = "su" + Guid.NewGuid().ToString("N")[..8], Email = $"su{Guid.NewGuid():N}@example.com" };

    [Fact]
    public async Task ASensitiveActionWithoutConfirmationIsRefusedAndSaysWhy()
    {
        var token = await AdminTokenAsync();

        var response = await SendAsync(HttpMethod.Post, "/api/admin/users", token, NewUser());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("step_up_required", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task AfterAConfirmationTheActionWorks()
    {
        var token = await AdminTokenAsync();
        var elevation = await api.AdminElevationAsync(token, Ct);

        var response = await SendAsync(HttpMethod.Post, "/api/admin/users", token, NewUser(), elevation);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task ReadingDoesNotNeedAConfirmation()
    {
        var response = await SendAsync(HttpMethod.Get, "/api/admin/users", await AdminTokenAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task TheConfirmationOfAnotherUserIsRefused()
    {
        var other = await api.SetUpAdministratorAsync("elevother", Ct);
        var othersElevation = await api.ElevationAsync(await AccessTokenAsync(other), other.AuthenticatorKey, Ct);

        var response = await SendAsync(HttpMethod.Post, "/api/admin/users", await AdminTokenAsync(), NewUser(), othersElevation);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("step_up_required", await response.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public async Task GarbageIsNotAConfirmation(string elevation)
    {
        var response = await SendAsync(HttpMethod.Post, "/api/admin/users", await AdminTokenAsync(), NewUser(), elevation);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnAccessTokenIsNotAConfirmationAndAConfirmationIsNotAnAccessToken()
    {
        var token = await AdminTokenAsync();
        var elevation = await api.AdminElevationAsync(token, Ct);

        var accessAsElevation = await SendAsync(HttpMethod.Post, "/api/admin/users", token, NewUser(), token);
        var elevationAsAccess = await SendAsync(HttpMethod.Get, "/api/account", elevation);

        Assert.Equal(HttpStatusCode.Forbidden, accessAsElevation.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, elevationAsAccess.StatusCode);
    }

    [Fact]
    public async Task AWrongCodeOrARecoveryCodeDoesNotConfirm()
    {
        var account = await api.SetUpAdministratorAsync("stepwrong", Ct);
        var token = await AccessTokenAsync(account);

        var wrong = await SendAsync(HttpMethod.Post, "/api/account/step-up", token, new { Code = "000000" });
        var recovery = await SendAsync(HttpMethod.Post, "/api/account/step-up", token, new { Code = account.RecoveryCodes[0], Method = "Recovery" });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, recovery.StatusCode);
    }

    [Fact]
    public async Task ConfirmingNeedsASignedInUser()
    {
        var response = await SendAsync(HttpMethod.Post, "/api/account/step-up", null, new { Code = "123456" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ACodeFromTheEmailConfirmsForAUserWhoTurnedEmailCodesOn()
    {
        var account = await api.SetUpAdministratorAsync("stepemail", Ct);
        var token = await AccessTokenAsync(account);
        var address = $"{account.UserName}@example.com";
        await SendAsync(HttpMethod.Post, "/api/account/email-codes/send", token);
        Assert.Equal(HttpStatusCode.NoContent, (await SendAsync(HttpMethod.Post, "/api/account/email-codes/enable", token, new { Code = api.Mail.LastCodeTo(address) })).StatusCode);
        var before = api.Mail.CountTo(address);

        var sent = await SendAsync(HttpMethod.Post, "/api/account/step-up/email", token);
        var confirmed = await SendAsync(HttpMethod.Post, "/api/account/step-up", token, new { Code = api.Mail.LastCodeTo(address), Method = "Email" });

        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);
        Assert.Equal(before + 1, api.Mail.CountTo(address));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.False(string.IsNullOrEmpty((await confirmed.Content.ReadFromJsonAsync<Elevation>(Ct))!.Token));
    }

    [Fact]
    public async Task NoEmailIsSentForAUserWhoDidNotTurnEmailCodesOn()
    {
        var account = await api.SetUpAdministratorAsync("stepnoemail", Ct);

        var sent = await SendAsync(HttpMethod.Post, "/api/account/step-up/email", await AccessTokenAsync(account));

        Assert.Equal(HttpStatusCode.Conflict, sent.StatusCode);
        Assert.Equal(0, api.Mail.CountTo($"{account.UserName}@example.com"));
    }
}
