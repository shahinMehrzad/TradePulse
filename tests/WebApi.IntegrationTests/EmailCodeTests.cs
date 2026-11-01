using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Application.Interfaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WebApi.IntegrationTests;

/// <summary>Sign-in codes by email: turning them on with a test mail, using one to sign in, and the limits around it.</summary>
public class EmailCodeTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Step(string Stage, string? ChallengeToken, bool EmailCodeAvailable);
    private record Info(bool EmailCodesEnabled, bool EmailAvailable);

    private static string AddressOf(ApiFixture.TestAccount account) => $"{account.UserName}@example.com";

    private async Task<HttpResponseMessage> PostAsync(string url, object? body, string? bearer = null, ApiFixture? host = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = body is null ? null : JsonContent.Create(body) };
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await (host ?? api).CreateClient().SendAsync(request, Ct);
    }

    private async Task<string> TokenAsync(ApiFixture.TestAccount account) =>
        (await (await api.LoginAsync(account, ApiFixture.NewUserPassword, Ct)).Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct))!.AccessToken;

    private async Task<Info> AccountAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/account");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await (await api.CreateClient().SendAsync(request, Ct)).Content.ReadFromJsonAsync<Info>(Ct))!;
    }

    /// <summary>A user with email codes on: the test mail is sent and its code entered.</summary>
    private async Task<ApiFixture.TestAccount> AccountWithEmailCodesAsync(string prefix)
    {
        var account = await api.SetUpAdministratorAsync(prefix, Ct);
        var token = await TokenAsync(account);
        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/api/account/email-codes/send", null, token)).StatusCode);
        var enabled = await PostAsync("/api/account/email-codes/enable", new { Code = api.Mail.LastCodeTo(AddressOf(account)) }, token);
        Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
        return account;
    }

    private async Task<Step> PasswordStepAsync(ApiFixture.TestAccount account) =>
        (await (await PostAsync("/api/auth/login", new { Email = "", Username = account.UserName, Password = ApiFixture.NewUserPassword })).Content.ReadFromJsonAsync<Step>(Ct))!;

    [Fact]
    public async Task EmailCodesAreOfferedAndOffByDefault()
    {
        var account = await api.SetUpAdministratorAsync("offbydefault", Ct);

        var info = await AccountAsync(await TokenAsync(account));
        var step = await PasswordStepAsync(account);

        Assert.True(info.EmailAvailable);
        Assert.False(info.EmailCodesEnabled);
        Assert.False(step.EmailCodeAvailable);
    }

    [Fact]
    public async Task ATestMailWithTheRightCodeTurnsEmailCodesOn()
    {
        var account = await api.SetUpAdministratorAsync("enable", Ct);
        var token = await TokenAsync(account);

        Assert.Equal(HttpStatusCode.NoContent, (await PostAsync("/api/account/email-codes/send", null, token)).StatusCode);
        Assert.Equal(1, api.Mail.CountTo(AddressOf(account)));
        var wrong = await PostAsync("/api/account/email-codes/enable", new { Code = "000000" }, token);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.False((await AccountAsync(token)).EmailCodesEnabled);

        var right = await PostAsync("/api/account/email-codes/enable", new { Code = api.Mail.LastCodeTo(AddressOf(account)) }, token);

        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        Assert.True((await AccountAsync(token)).EmailCodesEnabled);
    }

    [Fact]
    public async Task ACodeFromTheMailSignsTheUserIn()
    {
        var account = await AccountWithEmailCodesAsync("signin");
        var step = await PasswordStepAsync(account);
        Assert.Equal("TwoFactorRequired", step.Stage);
        Assert.True(step.EmailCodeAvailable);

        var sent = await PostAsync("/api/auth/login/two-factor/email", new { step.ChallengeToken });
        var code = api.Mail.LastCodeTo(AddressOf(account));
        var done = await PostAsync("/api/auth/login/two-factor", new { step.ChallengeToken, Code = code, Method = "Email" });

        Assert.Equal(HttpStatusCode.NoContent, sent.StatusCode);
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.False(string.IsNullOrEmpty((await done.Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct))!.AccessToken));
    }

    [Fact]
    public async Task AWrongEmailCodeIsRefused()
    {
        var account = await AccountWithEmailCodesAsync("wrongcode");
        var step = await PasswordStepAsync(account);

        var done = await PostAsync("/api/auth/login/two-factor", new { step.ChallengeToken, Code = "000000", Method = "Email" });

        Assert.Equal(HttpStatusCode.Unauthorized, done.StatusCode);
    }

    [Fact]
    public async Task WithoutTurningItOnNoEmailIsSentAndNoEmailCodeWorks()
    {
        var account = await api.SetUpAdministratorAsync("notenabled", Ct);
        var token = await TokenAsync(account);
        // The user asks for a test code (which proves nothing yet) and then tries to sign in with it.
        await PostAsync("/api/account/email-codes/send", null, token);
        var code = api.Mail.LastCodeTo(AddressOf(account));
        var before = api.Mail.CountTo(AddressOf(account));
        var step = await PasswordStepAsync(account);

        var send = await PostAsync("/api/auth/login/two-factor/email", new { step.ChallengeToken });
        var done = await PostAsync("/api/auth/login/two-factor", new { step.ChallengeToken, Code = code, Method = "Email" });

        Assert.Equal(HttpStatusCode.Conflict, send.StatusCode);
        Assert.Equal(before, api.Mail.CountTo(AddressOf(account)));
        Assert.Equal(HttpStatusCode.Unauthorized, done.StatusCode);
    }

    [Fact]
    public async Task CodesCannotBeAskedForInQuickSuccession()
    {
        var account = await api.SetUpAdministratorAsync("throttled", Ct);
        var token = await TokenAsync(account);

        var first = await PostAsync("/api/account/email-codes/send", null, token);
        var second = await PostAsync("/api/account/email-codes/send", null, token);

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, api.Mail.CountTo(AddressOf(account)));
    }

    [Fact]
    public async Task TurningThemOffNeedsThePassword()
    {
        var account = await AccountWithEmailCodesAsync("disable");
        var token = await TokenAsync(account);

        var wrong = await PostAsync("/api/account/email-codes/disable", new { Password = "NotMyPassword123!" }, token);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.True((await AccountAsync(token)).EmailCodesEnabled);

        var right = await PostAsync("/api/account/email-codes/disable", new { Password = ApiFixture.NewUserPassword }, token);

        Assert.Equal(HttpStatusCode.NoContent, right.StatusCode);
        Assert.False((await AccountAsync(token)).EmailCodesEnabled);
        Assert.False((await PasswordStepAsync(account)).EmailCodeAvailable);
    }

    [Fact]
    public async Task ARefusingMailServerIsAnAnswerNotACrash()
    {
        var account = await api.SetUpAdministratorAsync("refused", Ct);
        var token = await TokenAsync(account);
        api.Mail.Fails = true;
        try
        {
            var response = await PostAsync("/api/account/email-codes/send", null, token);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("could not be sent", await response.Content.ReadAsStringAsync(Ct));
        }
        finally
        {
            api.Mail.Fails = false;
        }
    }

    [Fact]
    public async Task WithoutMailSettingsTheFeatureIsNotOffered()
    {
        var account = await api.SetUpAdministratorAsync("nomail", Ct);
        var token = await TokenAsync(account);
        using var withoutMail = api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IMailSender>();
            services.AddSingleton<IMailSender>(new FakeMailSender { IsConfigured = false });
        }));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/account");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var info = (await (await withoutMail.CreateClient().SendAsync(request, Ct)).Content.ReadFromJsonAsync<Info>(Ct))!;
        var send = await withoutMail.CreateClient().SendAsync(
            new HttpRequestMessage(HttpMethod.Post, "/api/account/email-codes/send") { Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) } }, Ct);

        Assert.False(info.EmailAvailable);
        Assert.Equal(HttpStatusCode.Conflict, send.StatusCode);
    }
}
