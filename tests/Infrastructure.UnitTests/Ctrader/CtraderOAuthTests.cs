using System.Net;
using System.Text;
using Infrastructure.Exchange.Ctrader;

namespace Infrastructure.UnitTests.Ctrader;

public class CtraderOAuthTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private const string GoodAnswer = """{"accessToken":"AT-1","tokenType":"bearer","expiresIn":2628000,"refreshToken":"RT-1","errorCode":null,"description":null}""";

    [Theory]
    [InlineData(CtraderScope.Accounts, "scope=accounts")]
    [InlineData(CtraderScope.Trading, "scope=trading")]
    public void TheAuthorizationAddressCarriesTheApplicationTheRedirectAndTheScope(CtraderScope scope, string expected)
    {
        var url = CtraderOAuth.BuildAuthorizationUrl("my id", "http://localhost:5000/callback", scope);

        Assert.StartsWith(CtraderOAuth.AuthorizeEndpoint, url.ToString());
        Assert.Contains("client_id=my%20id", url.Query);
        Assert.Contains("redirect_uri=http%3A%2F%2Flocalhost%3A5000%2Fcallback", url.Query);
        Assert.Contains(expected, url.Query);
    }

    [Fact]
    public void TheCodeIsReadFromTheCallbackAndARefusalOrAnEmptyVisitIsNot()
    {
        Assert.True(CtraderOAuth.TryReadCallback(new Uri("http://localhost:5000/callback?code=abc%2B123"), out var code, out var none));
        Assert.Equal("abc+123", code);
        Assert.Null(none);

        Assert.False(CtraderOAuth.TryReadCallback(new Uri("http://localhost:5000/callback?error=access_denied&error_description=No"), out _, out var refused));
        Assert.Contains("access_denied", refused);
        Assert.Contains("No", refused);

        Assert.False(CtraderOAuth.TryReadCallback(new Uri("http://localhost:5000/favicon.ico"), out _, out var empty));
        Assert.Equal("The address has no code.", empty);
    }

    [Fact]
    public async Task ACodeIsExchangedForTheTokens()
    {
        var handler = new ScriptedHandler(_ => Json(GoodAnswer));

        var tokens = await CtraderOAuth.ExchangeCodeAsync(new HttpClient(handler), "id", "the secret", "code 1", "http://localhost:5000/callback", Ct);

        Assert.Equal("AT-1", tokens.AccessToken);
        Assert.Equal("RT-1", tokens.RefreshToken);
        Assert.Equal(TimeSpan.FromSeconds(2_628_000), tokens.ExpiresIn);
        var query = Assert.Single(handler.Requests).RequestUri!.Query;
        Assert.Contains("grant_type=authorization_code", query);
        Assert.Contains("code=code%201", query);
        Assert.Contains("client_secret=the%20secret", query);
    }

    [Fact]
    public async Task ARefreshSendsTheRefreshTokenAndNoCode()
    {
        var handler = new ScriptedHandler(_ => Json(GoodAnswer));

        await CtraderOAuth.RefreshAsync(new HttpClient(handler), "id", "secret", "RT-0", Ct);

        var query = Assert.Single(handler.Requests).RequestUri!.Query;
        Assert.Contains("grant_type=refresh_token", query);
        Assert.Contains("refresh_token=RT-0", query);
        Assert.DoesNotContain("code=", query);
    }

    [Fact]
    public async Task WhenGetIsNotAllowedItTriesPost()
    {
        var handler = new ScriptedHandler(r => r.Method == HttpMethod.Get ? new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) : Json(GoodAnswer));

        var tokens = await CtraderOAuth.RefreshAsync(new HttpClient(handler), "id", "secret", "RT-0", Ct);

        Assert.Equal("AT-1", tokens.AccessToken);
        Assert.Equal([HttpMethod.Get, HttpMethod.Post], handler.Requests.Select(r => r.Method));
    }

    [Fact]
    public async Task AnErrorAnswerBecomesAnExceptionWithCTradersCodeAndNoSecret()
    {
        var handler = new ScriptedHandler(_ => Json("""{"accessToken":null,"refreshToken":null,"expiresIn":0,"errorCode":"ACCESS_DENIED","description":"Invalid authorization code"}""", HttpStatusCode.BadRequest));

        var failure = await Assert.ThrowsAsync<CtraderException>(
            () => CtraderOAuth.ExchangeCodeAsync(new HttpClient(handler), "id", "TOP-SECRET", "code", "http://localhost:5000/callback", Ct));

        Assert.Equal("ACCESS_DENIED", failure.ErrorCode);
        Assert.Contains("Invalid authorization code", failure.Message);
        Assert.DoesNotContain("TOP-SECRET", failure.Message);
    }

    [Fact]
    public async Task ANetworkOrFormatFailureNeverCarriesTheAddressWithTheSecretInIt()
    {
        var down = new ScriptedHandler(_ => throw new HttpRequestException("failed to reach https://openapi.ctrader.com/apps/token?client_secret=TOP-SECRET"));
        var notJson = new ScriptedHandler(_ => Json("<html>bad gateway</html>", HttpStatusCode.BadGateway));

        var one = await Assert.ThrowsAsync<CtraderException>(() => CtraderOAuth.RefreshAsync(new HttpClient(down), "id", "TOP-SECRET", "rt", Ct));
        var two = await Assert.ThrowsAsync<CtraderException>(() => CtraderOAuth.RefreshAsync(new HttpClient(notJson), "id", "TOP-SECRET", "rt", Ct));

        Assert.DoesNotContain("TOP-SECRET", one.Message);
        Assert.DoesNotContain("TOP-SECRET", two.Message);
    }

    [Fact]
    public async Task AnAnswerWithoutTokensIsAnError()
    {
        var handler = new ScriptedHandler(_ => Json("""{"expiresIn":10}"""));

        await Assert.ThrowsAsync<CtraderException>(() => CtraderOAuth.RefreshAsync(new HttpClient(handler), "id", "secret", "rt", Ct));
    }

    [Fact]
    public void TheTokensDoNotPrintThemselves()
    {
        var text = new CtraderTokens("AT-secret", "RT-secret", TimeSpan.FromDays(30)).ToString();

        Assert.DoesNotContain("secret", text);
    }
}
