using System.Net;
using System.Net.Http.Json;
using Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace WebApi.IntegrationTests;

/// <summary>
/// What a copy of the database gives away about the second factor: neither the authenticator key nor a recovery code may be
/// readable in <c>auth.user_tokens</c>, and rows written as plain text before that rule are converted at the next start.
/// </summary>
public class StoredSecretsTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Password = "LegacyPassword123!";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Step(string Stage, string? ChallengeToken, string? AccessToken);

    private static async Task<List<IdentityUserToken<Guid>>> TokensOfAsync(WebApplicationFactory<Program> host, Guid userId)
    {
        using var scope = host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await context.UserTokens.AsNoTracking().Where(t => t.UserId == userId).ToListAsync(Ct);
    }

    private static async Task<HttpStatusCode> SignInAsync(HttpClient client, string userName, string code, string method)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new { Email = "", Username = userName, Password }, Ct);
        var step = (await login.Content.ReadFromJsonAsync<Step>(Ct))!;
        var second = await client.PostAsJsonAsync("/api/auth/login/two-factor", new { step.ChallengeToken, Code = code, Method = method }, Ct);
        return second.StatusCode;
    }

    [Fact]
    public async Task TheDatabaseHoldsNeitherTheAuthenticatorKeyNorTheRecoveryCodes()
    {
        var account = await api.SetUpAdministratorAsync("stored", Ct);

        var tokens = await TokensOfAsync(api, account.Id);

        Assert.Contains(tokens, t => t.Name == "AuthenticatorKey" && !string.IsNullOrEmpty(t.Value));
        Assert.Contains(tokens, t => t.Name == "RecoveryCodes" && !string.IsNullOrEmpty(t.Value));
        Assert.All(tokens, t =>
        {
            Assert.DoesNotContain(account.AuthenticatorKey, t.Value ?? "", StringComparison.OrdinalIgnoreCase);
            Assert.All(account.RecoveryCodes, code => Assert.DoesNotContain(code, t.Value ?? "", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public async Task PlainTextSecretsFromBeforeAreProtectedAtStartAndStillWork()
    {
        const string key = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
        string[] codes = ["BCDFG-HJKMN", "PQRTV-WXY23"];
        var userName = "legacy" + Guid.NewGuid().ToString("N")[..8];

        // An account as an older version left it: two-factor on, key and codes in plain text.
        Guid userId;
        using (var scope = api.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = userName, Email = $"{userName}@example.com", EmailConfirmed = true, MustChangePassword = false, TwoFactorEnabled = true };
            Assert.True((await users.CreateAsync(user, Password)).Succeeded);
            userId = user.Id;

            var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            context.UserTokens.AddRange(
                new IdentityUserToken<Guid> { UserId = userId, LoginProvider = "[AspNetUserStore]", Name = "AuthenticatorKey", Value = key },
                new IdentityUserToken<Guid> { UserId = userId, LoginProvider = "[AspNetUserStore]", Name = "RecoveryCodes", Value = string.Join(';', codes) });
            await context.SaveChangesAsync(Ct);
        }

        // A new start of the API converts them; everything below goes through that host.
        await using var restarted = api.WithWebHostBuilder(_ => { });
        using var client = restarted.CreateClient();

        var tokens = await TokensOfAsync(restarted, userId);
        Assert.All(tokens, t =>
        {
            Assert.DoesNotContain(key, t.Value ?? "");
            Assert.All(codes, code => Assert.DoesNotContain(code, t.Value ?? ""));
        });

        Assert.Equal(HttpStatusCode.OK, await SignInAsync(client, userName, Totp.Code(key), "Authenticator"));
        Assert.Equal(HttpStatusCode.OK, await SignInAsync(client, userName, codes[1], "Recovery"));
        Assert.Equal(HttpStatusCode.Unauthorized, await SignInAsync(client, userName, codes[1], "Recovery"));
    }
}
