using System.Net;
using System.Text;
using System.Text.Json;
using Application.Signals;
using Domain.Enums;

namespace SignalReceiver.IntegrationTests;

/// <summary>The webhook over HTTP, through the whole pipeline of the program: what a sender sends and what it is answered.</summary>
public class WebhookTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string SignalId = "S1:BTCUSDT:4h:2026-11-07T04:00Z:long";
    private const string WebhookPath = "/api/signals";

    private readonly ReceiverFactory _factory = new();

    public WebhookTests() => _factory.Board.Set(ExchangeEnum.Binance, "BTC/USDT", 100m, ReceiverFactory.Now.AddSeconds(-1));

    public void Dispose() => _factory.Dispose();

    /// <summary>A long at 100 (stop 98, target 104) of the 4h candle that closed at 08:00.</summary>
    private static string Body(Action<Dictionary<string, object?>>? change = null)
    {
        var fields = new Dictionary<string, object?>
        {
            ["signalId"] = SignalId,
            ["strategy"] = "S1",
            ["market"] = "Futures",
            ["symbol"] = "BTC/USDT",
            ["side"] = "Long",
            ["leverage"] = 5,
            ["takeProfit"] = 104,
            ["stopLoss"] = 98,
            ["signalPrice"] = 100,
            ["timeframe"] = "4h",
            ["candleOpenTime"] = "2026-11-07T04:00:00Z",
            ["candleCloseTime"] = "2026-11-07T08:00:00Z",
            ["validUntil"] = "2026-11-07T08:45:00Z",
        };
        change?.Invoke(fields);
        return JsonSerializer.Serialize(fields);
    }

    private static HttpRequestMessage Request(string body, string? key = SignalId, string secret = ReceiverFactory.Secret, string? stamp = "2026-11-07T08:00:02Z", bool sign = true)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, WebhookPath) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (stamp is not null)
            request.Headers.Add("X-Timestamp", stamp);
        if (sign && stamp is not null)
            request.Headers.Add("X-Signature", SignalAuthenticator.Sign(secret, stamp, body));
        if (key is not null)
            request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private async Task<(HttpResponseMessage Response, JsonElement Json)> SendAsync(HttpRequestMessage request)
    {
        using var client = _factory.CreateClient();
        var response = await client.SendAsync(request, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task ASignedSignalThatIsWorthTakingIsAcceptedAndItsTradeIsOpen()
    {
        var (response, json) = await SendAsync(Request(Body()));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(SignalId, json.GetProperty("signalId").GetString());
        var trade = Assert.Single(_factory.Store.Trades);
        Assert.Equal(trade.Id, json.GetProperty("tradeId").GetInt32());
        Assert.Equal(100m, trade.EntryPrice);
        Assert.Same(trade, Assert.Single(_factory.Tracked));
        Assert.Same(trade, Assert.Single(_factory.Told));
    }

    [Fact]
    public async Task TheSameSignalSentAgainIsToldItIsKnownAndNothingIsDoneTwice()
    {
        await SendAsync(Request(Body()));

        var (response, json) = await SendAsync(Request(Body(), stamp: "2026-11-07T08:00:30Z"));   // a retry: a new time, a new signature

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("duplicate", json.GetProperty("status").GetString());
        Assert.Single(_factory.Store.Trades);
        Assert.Single(_factory.Tracked);
    }

    [Fact]
    public async Task ASignalThatIsRefusedIsAnswered422WithTheReason()
    {
        _factory.Board.Set(ExchangeEnum.Binance, "BTC/USDT", 103m, ReceiverFactory.Now);

        var (response, json) = await SendAsync(Request(Body()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("PriceMovedTooFar", json.GetProperty("reason").GetString());
        Assert.Empty(_factory.Store.Trades);
        Assert.Equal(SignalRejectionEnum.PriceMovedTooFar, _factory.Store.Signals[SignalId].Rejection);
    }

    [Fact]
    public async Task AStrategyNobodyAddedIsRefused()
    {
        var body = Body(f => { f["strategy"] = "S9"; f["signalId"] = "S9:BTCUSDT:4h:2026-11-07T04:00Z:long"; });

        var (response, json) = await SendAsync(Request(body, key: "S9:BTCUSDT:4h:2026-11-07T04:00Z:long"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("UnknownStrategy", json.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task WithoutARecentPriceTheAnswerIs503AndTheSenderIsToldWhenToTryAgain()
    {
        _factory.Board.Set(ExchangeEnum.Binance, "BTC/USDT", 100m, ReceiverFactory.Now.AddMinutes(-5));

        var (response, json) = await SendAsync(Request(Body()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("5", Assert.Single(response.Headers.GetValues("Retry-After")));
        Assert.Equal("no_recent_price", json.GetProperty("error").GetString());
        Assert.Empty(_factory.Store.Signals);
    }

    // ---- who and what -------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ARequestWithoutASignatureIs401()
    {
        var (response, json) = await SendAsync(Request(Body(), sign: false));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", json.GetProperty("error").GetString());
        Assert.Empty(_factory.Store.Signals);
    }

    [Fact]
    public async Task ARequestSignedWithAnotherSecretIs401()
    {
        var (response, _) = await SendAsync(Request(Body(), secret: "another-secret-that-is-long-enough-to-be-real"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ARequestWhoseTimeIsTooOldIs401()
    {
        var (response, _) = await SendAsync(Request(Body(), stamp: "2026-11-07T07:50:00Z"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ASignedBodyThatIsNotTheContractIs400WithTheFieldNamed()
    {
        var (response, json) = await SendAsync(Request(Body(f => f.Remove("stopLoss"))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("stopLoss", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ASignedBodyThatIsNotJsonIs400()
    {
        var (response, _) = await SendAsync(Request("this is not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AKeyThatIsNotTheSignalIdIs400()
    {
        var (response, json) = await SendAsync(Request(Body(), key: "another-key"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Idempotency-Key", json.GetProperty("error").GetString());
    }

    [Fact]
    public async Task ABodyLargerThanASignalIs413BeforeAnythingIsChecked()
    {
        var (response, _) = await SendAsync(Request(new string('x', 20_000), sign: false));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task OnlyPostIsAnswered()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync(WebhookPath, Ct);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task AnUnknownPathIs404()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/anything-else", new StringContent("{}"), Ct)).StatusCode);
    }

    [Fact]
    public async Task TheHealthCheckAnswersWithoutASignature()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health", Ct)).StatusCode);
    }

    // ---- starting -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WithoutASecretTheProgramDoesNotStart()
    {
        using var factory = new ReceiverFactory(secret: null);

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Signals:Webhook:Secret", Flatten(error));
    }

    [Fact]
    public void ASecretThatIsTooShortDoesNotStartTheProgramEither()
    {
        using var factory = new ReceiverFactory(secret: "too-short");

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("at least 32", Flatten(error));
        Assert.DoesNotContain("too-short", Flatten(error));   // the secret is never in an error
    }

    private static string Flatten(Exception error)
    {
        var parts = new List<string>();
        for (var e = error; e is not null; e = e.InnerException!)
            parts.Add(e.Message);

        return string.Join(" | ", parts);
    }
}
