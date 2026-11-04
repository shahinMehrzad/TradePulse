using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>A sign-in with a code from the app already is the step-up, so the user is not asked for a second code within the same 30 seconds.</summary>
public class SignInStepUpTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Login(string AccessToken, string RefreshToken, string? ElevationToken, DateTime? ElevationExpiresAt);

    private async Task<HttpResponseMessage> CreateUserAsync(string accessToken, string? elevation)
    {
        var name = "stepup" + Guid.NewGuid().ToString("N")[..8];
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/users") { Content = JsonContent.Create(new { UserName = name, Email = $"{name}@example.com" }) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (elevation is not null)
            request.Headers.Add("X-Elevation", elevation);
        return await api.CreateClient().SendAsync(request, Ct);
    }

    [Fact]
    public async Task TheCodeThatSignedTheUserInIsAlsoTheStepUp()
    {
        var response = await api.AdminLoginAsync(api.CreateClient(), Ct, "device-" + Guid.NewGuid().ToString("N")[..10]);
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<Login>(Ct))!;

        Assert.False(string.IsNullOrEmpty(login.ElevationToken));
        Assert.InRange(login.ElevationExpiresAt!.Value, DateTime.UtcNow.AddMinutes(8), DateTime.UtcNow.AddMinutes(11));

        Assert.Equal(HttpStatusCode.Forbidden, (await CreateUserAsync(login.AccessToken, elevation: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CreateUserAsync(login.AccessToken, login.ElevationToken)).StatusCode);
    }

    [Fact]
    public async Task ARefreshGivesNoStepUp()
    {
        var device = "device-" + Guid.NewGuid().ToString("N")[..10];
        var login = (await (await api.AdminLoginAsync(api.CreateClient(), Ct, device)).Content.ReadFromJsonAsync<Login>(Ct))!;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/refresh-token") { Content = JsonContent.Create(new { login.RefreshToken }) };
        request.Headers.Add("X-Device-Id", device);
        var refreshed = (await (await api.CreateClient().SendAsync(request, Ct)).Content.ReadFromJsonAsync<Login>(Ct))!;

        Assert.Null(refreshed.ElevationToken);
    }
}
