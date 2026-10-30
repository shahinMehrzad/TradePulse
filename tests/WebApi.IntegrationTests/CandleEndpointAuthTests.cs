using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace WebApi.IntegrationTests;

public class CandleEndpointAuthTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    // Same values as appsettings.json / appsettings.Development.json.
    private const string Issuer = "TradePulseApi";
    private const string Audience = "TradePulseClient";
    private const string Secret = ApiFixture.JwtSecret;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);

    private async Task<HttpResponseMessage> GetAsync(string url, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);

        return await api.CreateClient().SendAsync(request, Ct);
    }

    private async Task<string> LoginAsync()
    {
        var response = await api.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { Email = ApiFixture.AdminEmail, Username = "", Password = ApiFixture.AdminPassword }, Ct);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken;
    }

    private static string Token(
        string secret = Secret, string issuer = Issuer, string audience = Audience, TimeSpan? lifetime = null, string algorithm = SecurityAlgorithms.HmacSha256)
    {
        var expires = DateTime.UtcNow + (lifetime ?? TimeSpan.FromMinutes(10));
        var notBefore = expires < DateTime.UtcNow ? expires.AddMinutes(-30) : DateTime.UtcNow.AddMinutes(-1);

        var token = new JwtSecurityToken(
            issuer, audience, [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], notBefore, expires,
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), algorithm));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Theory]
    [InlineData(ApiFixture.CandleUrl)]
    [InlineData("/api/candles/binance/futures/btcusdt/1h?limit=5")]
    public async Task WithoutATokenTheCandleEndpointsAreUnauthorized(string url)
    {
        var response = await GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ATokenFromLoginOpensTheCandleEndpoints()
    {
        var token = await LoginAsync();

        var latest = await GetAsync(ApiFixture.CandleUrl, token);
        var range = await GetAsync("/api/candles/binance/futures/btcusdt/1h?limit=3", token);

        Assert.Equal(HttpStatusCode.OK, latest.StatusCode);
        Assert.Equal(HttpStatusCode.OK, range.StatusCode);
        Assert.Contains("openTime", await latest.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task AuthenticatedRequestsStillGetNormalErrors()
    {
        var token = await LoginAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync("/api/candles/binance/futures/btcusdt/1h?limit=0", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("/api/candles/binance/futures/ethusdt/4h/latest", token)).StatusCode);
    }

    [Fact]
    public async Task LoginItselfStaysOpen()
    {
        var wrong = await api.CreateClient().PostAsJsonAsync(
            "/api/auth/login", new { Email = ApiFixture.AdminEmail, Username = "", Password = "wrong" }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode); // reached the handler, not blocked by the auth middleware
    }

    [Fact]
    public async Task TheTestHelperProducesAValidTokenSoTheRejectionsBelowAreMeaningful()
    {
        var response = await GetAsync(ApiFixture.CandleUrl, Token());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static TheoryData<string, string> BadTokens() => new()
    {
        { "signed with another key", Token(secret: "Another-signing-key-that-is-long-enough-0123456789") },
        { "wrong issuer", Token(issuer: "SomeoneElse") },
        { "wrong audience", Token(audience: "SomeoneElse") },
        { "expired", Token(lifetime: TimeSpan.FromMinutes(-10)) },
        { "valid signature but a different HMAC algorithm", Token(algorithm: SecurityAlgorithms.HmacSha384) },
        { "garbage", "not.a.token" },
        { "unsigned (alg none)", UnsignedToken() },
    };

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task InvalidTokensAreRejected(string description, string token)
    {
        var response = await GetAsync(ApiFixture.CandleUrl, token);

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{description}: got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task ATamperedTokenIsRejected()
    {
        var parts = (await LoginAsync()).Split('.');
        var payload = parts[1].Length > 8 ? parts[1][..^4] + (parts[1][^4] == 'A' ? 'B' : 'A') + parts[1][^3..] : parts[1];
        var tampered = $"{parts[0]}.{payload}.{parts[2]}";

        var response = await GetAsync(ApiFixture.CandleUrl, tampered);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static string UnsignedToken()
    {
        static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        return $"{B64("{\"alg\":\"none\",\"typ\":\"JWT\"}")}.{B64($"{{\"iss\":\"{Issuer}\",\"aud\":\"{Audience}\",\"exp\":{exp}}}")}.";
    }
}
