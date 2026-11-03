using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Tests.Shared;

namespace WebApi.IntegrationTests;

public class CandleEndpointAuthTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    // Same values as appsettings.json / appsettings.Development.json.
    private const string Issuer = "TradePulseApi";
    private const string Audience = "TradePulseClient";

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
        var response = await api.AdminLoginAsync(api.CreateClient(), Ct);
        response.EnsureSuccessStatusCode();

        return (await response.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken;
    }

    /// <summary>A token as the API makes them (ES256, with the key of the test host) unless a test says otherwise.</summary>
    /// <remarks>The token names <paramref name="userId"/>, which has to be a user that exists: the API asks, so with a made-up user every token
    /// would be refused for that reason, and a test about the signature would prove nothing.</remarks>
    private static string Token(
        Guid userId, JwtTestKeys? keys = null, string issuer = Issuer, string audience = Audience, TimeSpan? lifetime = null, string algorithm = SecurityAlgorithms.EcdsaSha256)
        => Sign(userId, new SigningCredentials(new ECDsaSecurityKey((keys ?? ApiFixture.SigningKeys).Key), algorithm), issuer, audience, lifetime);

    private static string Sign(Guid userId, SigningCredentials credentials, string issuer = Issuer, string audience = Audience, TimeSpan? lifetime = null)
    {
        var expires = DateTime.UtcNow + (lifetime ?? TimeSpan.FromMinutes(10));
        var notBefore = expires < DateTime.UtcNow ? expires.AddMinutes(-30) : DateTime.UtcNow.AddMinutes(-1);

        var token = new JwtSecurityToken(
            issuer, audience, [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], notBefore, expires, credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>The public key is not secret. Anyone who has it can sign an HS256 token with its bytes as the "secret": the API must not take that for a signature.</summary>
    private static string HmacTokenWithThePublicKeyAsSecret(Guid userId, string algorithm) => Sign(userId, new SigningCredentials(
        new SymmetricSecurityKey(Convert.FromBase64String(ApiFixture.SigningKeys.PublicKey)), algorithm));

    private async Task<Guid> ARealUserAsync() => (await api.SetUpAdministratorAsync("tokens", Ct)).Id;

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
        var response = await GetAsync(ApiFixture.CandleUrl, Token(await ARealUserAsync()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static TheoryData<string> BadTokens() =>
    [
        "signed with another key", "wrong issuer", "wrong audience", "expired", "valid signature but a different ECDSA algorithm (ES384)",
        "HS256 signed with the public key as the secret", "HS512 signed with the public key as the secret", "garbage", "unsigned (alg none)"
    ];

    private static string BadToken(string description, Guid user) => description switch
    {
        "signed with another key" => Token(user, keys: JwtTestKeys.New()),
        "wrong issuer" => Token(user, issuer: "SomeoneElse"),
        "wrong audience" => Token(user, audience: "SomeoneElse"),
        "expired" => Token(user, lifetime: TimeSpan.FromMinutes(-10)),
        "valid signature but a different ECDSA algorithm (ES384)" => Token(user, algorithm: SecurityAlgorithms.EcdsaSha384),
        "HS256 signed with the public key as the secret" => HmacTokenWithThePublicKeyAsSecret(user, SecurityAlgorithms.HmacSha256),
        "HS512 signed with the public key as the secret" => HmacTokenWithThePublicKeyAsSecret(user, SecurityAlgorithms.HmacSha512),
        "garbage" => "not.a.token",
        "unsigned (alg none)" => UnsignedToken(),
        _ => throw new ArgumentOutOfRangeException(nameof(description))
    };

    [Theory]
    [MemberData(nameof(BadTokens))]
    public async Task InvalidTokensAreRejected(string description)
    {
        // For a user that exists: the refusal has to be about the token, not about a user nobody knows.
        var response = await GetAsync(ApiFixture.CandleUrl, BadToken(description, await ARealUserAsync()));

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{description}: got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task ATokenForAUserThatDoesNotExistIsRejectedEvenWithAGoodSignature()
    {
        var response = await GetAsync(ApiFixture.CandleUrl, Token(Guid.NewGuid()));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
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
