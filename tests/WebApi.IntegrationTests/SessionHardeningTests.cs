using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Application.Services;
using Dapper;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace WebApi.IntegrationTests;

/// <summary>
/// Signing out ends the session at the API, and a refresh token that was already renewed and shows up again (a copy of the session) ends it too
/// and says so. Each test has a device of its own: the sessions of the seeded administrator on other devices are not touched.
/// </summary>
public class SessionHardeningTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Tokens(string AccessToken, string RefreshToken);

    private static string NewDevice() => "device-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<Tokens> SignInAsync(string device)
    {
        var response = await api.AdminLoginAsync(api.CreateClient(), Ct, device);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Tokens>(Ct))!;
    }

    private async Task<HttpResponseMessage> RefreshAsync(string refreshToken, string device)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh-token") { Content = JsonContent.Create(new { RefreshToken = refreshToken }) };
        request.Headers.Add("X-Device-Id", device);
        return await api.CreateClient().SendAsync(request, Ct);
    }

    private async Task<HttpResponseMessage> LogoutAsync(string? accessToken, string? device)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/account/logout");
        if (accessToken is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (device is not null)
            request.Headers.Add("X-Device-Id", device);
        return await api.CreateClient().SendAsync(request, Ct);
    }

    private async Task AgeTheUseOfAsync(string refreshToken, TimeSpan by)
    {
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.ExecuteAsync(
            """UPDATE auth.refresh_tokens SET "UsedAt" = now() - @by WHERE "Token" = @hash""", new { by, hash = RefreshTokenHasher.Hash(refreshToken) });
    }

    private int AlertsAboutTheAdministrator => api.Alerts.About("admin").Count(a => a.Title.StartsWith("Session copied", StringComparison.Ordinal));

    // ---- signing out

    [Fact]
    public async Task SigningOutEndsThatDeviceAndNoOther()
    {
        var laptop = NewDevice();
        var phone = NewDevice();
        var onLaptop = await SignInAsync(laptop);
        var onPhone = await SignInAsync(phone);

        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(onLaptop.AccessToken, laptop)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(onLaptop.RefreshToken, laptop)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(onPhone.RefreshToken, phone)).StatusCode);
    }

    [Fact]
    public async Task ASignedOutTokenShownAgainIsRefusedWithoutAnAlarm()
    {
        var device = NewDevice();
        var session = await SignInAsync(device);
        await LogoutAsync(session.AccessToken, device);
        var before = AlertsAboutTheAdministrator;

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(session.RefreshToken, device)).StatusCode);

        Assert.Equal(before, AlertsAboutTheAdministrator);   // nothing was renewed, so there is no copy to worry about
    }

    [Fact]
    public async Task SigningOutNeedsASignedInUser()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await LogoutAsync(null, NewDevice())).StatusCode);
    }

    [Fact]
    public async Task SigningOutWithoutNamingTheDeviceEndsNothingAndIsNotAnError()
    {
        var device = NewDevice();
        var session = await SignInAsync(device);

        Assert.Equal(HttpStatusCode.NoContent, (await LogoutAsync(session.AccessToken, null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(session.RefreshToken, device)).StatusCode);
    }

    // ---- a token that was already renewed

    [Fact]
    public async Task ARenewedTokenShownAgainLaterEndsTheDevicesSessionSaysSoAndLeavesOtherDevicesAlone()
    {
        var device = NewDevice();
        var other = NewDevice();
        var first = await SignInAsync(device);
        var elsewhere = await SignInAsync(other);
        var renewed = await (await RefreshAsync(first.RefreshToken, device)).Content.ReadFromJsonAsync<Tokens>(Ct);
        await AgeTheUseOfAsync(first.RefreshToken, TimeSpan.FromMinutes(5));
        var before = AlertsAboutTheAdministrator;

        // Somebody has a copy of the first token. It is refused, and so is the renewed one: the session is over for both holders.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(first.RefreshToken, device)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(renewed!.RefreshToken, device)).StatusCode);

        Assert.Equal(before + 1, AlertsAboutTheAdministrator);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(elsewhere.RefreshToken, other)).StatusCode);

        using var scope = api.Services.CreateScope();
        var audit = await scope.ServiceProvider.GetRequiredService<ConfigDbContext>().AuditLog.AsNoTracking()
            .Where(e => e.UserName == "system" && e.Summary == "admin" && e.Details!.Contains("already used")).ToListAsync(Ct);
        Assert.NotEmpty(audit);
    }

    [Fact]
    public async Task ARenewedTokenShownAgainWithinSecondsIsOnlyRefused()
    {
        var device = NewDevice();
        var first = await SignInAsync(device);
        var renewed = await (await RefreshAsync(first.RefreshToken, device)).Content.ReadFromJsonAsync<Tokens>(Ct);
        var before = AlertsAboutTheAdministrator;

        // An answer that was lost and asked for again, or two requests that crossed: not a theft, and the new token is still good.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(first.RefreshToken, device)).StatusCode);

        Assert.Equal(before, AlertsAboutTheAdministrator);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(renewed!.RefreshToken, device)).StatusCode);
    }

    [Fact]
    public async Task AThiefMayNameAnyDeviceAndTheTokenIsStillRecognisedAsReused()
    {
        var device = NewDevice();
        var first = await SignInAsync(device);
        await RefreshAsync(first.RefreshToken, device);
        await AgeTheUseOfAsync(first.RefreshToken, TimeSpan.FromMinutes(5));
        var before = AlertsAboutTheAdministrator;

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(first.RefreshToken, NewDevice())).StatusCode);

        Assert.Equal(before + 1, AlertsAboutTheAdministrator);
    }
}
