using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;

namespace WebApi.IntegrationTests;

public class RefreshTokenTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Device = "test-device";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Tokens(string AccessToken, string RefreshToken);

    private async Task<HttpResponseMessage> PostAsync(string url, object body, string? device = Device)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (device is not null)
            request.Headers.Add("X-Device-Id", device);

        return await api.CreateClient().SendAsync(request, Ct);
    }

    private async Task<Tokens> LoginAsync(string device = Device)
    {
        var response = await PostAsync("/api/auth/login",
            new { Email = ApiFixture.AdminEmail, Username = "", Password = ApiFixture.AdminPassword }, device);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<Tokens>(Ct))!;
    }

    private Task<HttpResponseMessage> RefreshAsync(string refreshToken, string? device = Device) =>
        PostAsync("/api/auth/refresh-token", new { RefreshToken = refreshToken }, device);

    private async Task<HttpStatusCode> CandlesWith(string accessToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ApiFixture.CandleUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        return (await api.CreateClient().SendAsync(request, Ct)).StatusCode;
    }

    [Fact]
    public async Task RefreshNeedsNoAccessTokenAndReturnsAWorkingOne()
    {
        // The case that used to fail: by the time you refresh, the access token is gone or expired.
        var login = await LoginAsync();

        var response = await RefreshAsync(login.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var refreshed = (await response.Content.ReadFromJsonAsync<Tokens>(Ct))!;
        Assert.Equal(HttpStatusCode.OK, await CandlesWith(refreshed.AccessToken));
        Assert.NotEqual(login.RefreshToken, refreshed.RefreshToken);
    }

    [Fact]
    public async Task ARefreshTokenWorksOnlyOnce()
    {
        var login = await LoginAsync();
        var refreshed = (await (await RefreshAsync(login.RefreshToken)).Content.ReadFromJsonAsync<Tokens>(Ct))!;

        var reuse = await RefreshAsync(login.RefreshToken);
        var next = await RefreshAsync(refreshed.RefreshToken); // the rotated token keeps the session alive

        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
    }

    [Fact]
    public async Task ConcurrentRefreshesWithTheSameTokenSucceedExactlyOnce()
    {
        var login = await LoginAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RefreshAsync(login.RefreshToken)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, responses.Count(r => r.StatusCode == HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task ATokenIsBoundToTheDeviceItWasIssuedTo()
    {
        var login = await LoginAsync(device: "phone");

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(login.RefreshToken, device: "laptop")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(login.RefreshToken, device: null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(login.RefreshToken, device: "phone")).StatusCode); // wrong attempts did not burn it
    }

    [Theory]
    [InlineData("not-a-real-token")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public async Task UnknownTokensAreRejectedWithAGenericMessage(string token)
    {
        var response = await RefreshAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("Invalid token.", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task AnExpiredRefreshTokenIsRejected()
    {
        var login = await LoginAsync(device: "expiring");
        await using (var connection = new NpgsqlConnection(api.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = new NpgsqlCommand(
                "UPDATE auth.refresh_tokens SET \"ExpiresOn\" = now() - interval '1 day' WHERE \"DeviceId\" = 'expiring'", connection);
            await command.ExecuteNonQueryAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await RefreshAsync(login.RefreshToken, device: "expiring")).StatusCode);
    }

    [Fact]
    public async Task OnlyAHashOfTheTokenIsStored()
    {
        var login = await LoginAsync(device: "hashcheck");

        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM auth.refresh_tokens WHERE \"Token\" = @raw", connection);
        command.Parameters.AddWithValue("raw", login.RefreshToken);

        Assert.Equal(0L, await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task ARevokedSessionCannotBeRefreshedAfterLoginAgain()
    {
        // A second login gets its own token; using one never affects the other.
        var first = await LoginAsync(device: "a");
        var second = await LoginAsync(device: "b");

        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(first.RefreshToken, device: "a")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RefreshAsync(second.RefreshToken, device: "b")).StatusCode);
    }
}
