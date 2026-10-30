using System.Net;
using System.Net.Http.Json;
using Application.Commands.Users;
using Application.Interfaces;
using Domain.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using WebApi.Authentication;

namespace WebApi.IntegrationTests;

/// <summary>The way back in from the server's command line, for the administrator who lost the phone and the recovery codes.</summary>
public class AccessRecoveryTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Step(string Stage, bool MustChangePassword, bool NeedsTwoFactorSetup);

    private async Task<TemporaryPasswordDto> RecoverAsync(string userNameOrEmail)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ICommandDispatcher>()
            .SendAsync<RecoverAccessCommand, TemporaryPasswordDto>(new RecoverAccessCommand(userNameOrEmail), Ct);
    }

    private async Task<HttpResponseMessage> LoginAsync(string userName, string password) =>
        await api.CreateClient().PostAsJsonAsync("/api/auth/login", new { Email = "", Username = userName, Password = password }, Ct);

    [Fact]
    public async Task ALockedOutDisabledUserWithoutTheirPhoneGetsBackInThroughSetup()
    {
        var account = await api.SetUpAdministratorAsync("lost", Ct);
        var admin = (await (await api.AdminLoginAsync(api.CreateClient(), Ct)).Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct))!.AccessToken;
        using (var disable = new HttpRequestMessage(HttpMethod.Post, $"/api/admin/users/{account.Id}/disable"))
        {
            disable.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin);
            Assert.Equal(HttpStatusCode.NoContent, (await api.CreateClient().SendAsync(disable, Ct)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(account.UserName, ApiFixture.NewUserPassword)).StatusCode);

        var recovered = await RecoverAsync(account.UserName);

        Assert.Equal(account.UserName, recovered.UserName);
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(account.UserName, ApiFixture.NewUserPassword)).StatusCode); // the old password is gone
        var step = (await (await LoginAsync(account.UserName, recovered.TemporaryPassword)).Content.ReadFromJsonAsync<Step>(Ct))!;
        Assert.Equal("SetupRequired", step.Stage);
        Assert.True(step.MustChangePassword);
        Assert.True(step.NeedsTwoFactorSetup); // the lost authenticator and the recovery codes are worth nothing now
    }

    [Fact]
    public async Task TheUserCanBeNamedByEmailToo()
    {
        var account = await api.SetUpAdministratorAsync("byemail", Ct);

        var recovered = await RecoverAsync($"{account.UserName}@example.com");

        Assert.Equal(account.UserName, recovered.UserName);
    }

    [Fact]
    public async Task SignedInSessionsEndWithTheRecovery()
    {
        var account = await api.SetUpAdministratorAsync("sessions", Ct);
        var session = (await (await api.LoginAsync(account, ApiFixture.NewUserPassword, Ct, device: "old-phone")).Content.ReadFromJsonAsync<ApiFixture.Tokens>(Ct))!;

        await RecoverAsync(account.UserName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh-token") { Content = JsonContent.Create(new { session.RefreshToken }) };
        request.Headers.Add("X-Device-Id", "old-phone");
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().SendAsync(request, Ct)).StatusCode);
    }

    [Fact]
    public async Task AnUnknownUserIsAnErrorAndTheCommandExitsWithOne()
    {
        await Assert.ThrowsAsync<ConfigurationNotFoundException>(() => RecoverAsync("nobody-like-this"));

        Assert.Equal(1, await AccessRecovery.RunAsync(api.Services, "nobody-like-this"));
    }

    [Fact]
    public async Task TheCommandPrintsTheOneTimePasswordAndExitsWithZero()
    {
        var account = await api.SetUpAdministratorAsync("printed", Ct);
        var original = Console.Out;
        using var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            Assert.Equal(0, await AccessRecovery.RunAsync(api.Services, account.UserName));
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains($"'{account.UserName}'", captured.ToString());
        Assert.Contains("One-time password: ", captured.ToString());
    }
}
