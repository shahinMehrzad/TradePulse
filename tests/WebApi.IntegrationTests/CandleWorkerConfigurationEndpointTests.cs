using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

public class CandleWorkerConfigurationEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string ListUrl = "/api/admin/configurations/candle-worker";
    private const string AddUrl = "/api/admin/configurations/add-config";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Row(int Id, int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, DateTime StartDate);
    private record Created(int Id);
    private record Problem(int Status, string? Title, string? Detail);

    // Binance / Futures / BTCUSDT / 4h ... use values from the lookups; each test picks its own series because the database is shared.
    private static object Series(int symbolId, int timeFrameId, string? startDate = "2020-01-01T00:00:00", int exchangeId = 1, int marketId = 2) =>
        new { exchangeId, marketId, symbolId, timeFrameId, startDate };

    private async Task<HttpClient> AdminAsync()
    {
        var client = api.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { Email = ApiFixture.AdminEmail, Username = "", Password = ApiFixture.AdminPassword }, Ct);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Row[]> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<Row[]>(ListUrl, Ct))!;

    [Fact]
    public async Task WithoutATokenBothEndpointsAreUnauthorized()
    {
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ListUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(AddUrl, Series(1, 3600), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("2020-01-01T00:00:00Z", 60)]            // what a browser or any JSON client sends
    [InlineData("2020-01-01T00:00:00", 180)]
    [InlineData("2020-01-01T03:30:00+03:30", 300)]      // an offset: the same instant as midnight UTC
    public async Task AValidSeriesIsSavedAndItsIdIsReturned(string startDate, int timeFrameId)
    {
        var client = await AdminAsync();

        var response = await client.PostAsJsonAsync(AddUrl, Series(symbolId: 1, timeFrameId, startDate), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
        Assert.True(id > 0, "the new row's id should come back, not a row count or 0");

        var saved = Assert.Single(await ListAsync(client), r => r.Id == id);
        Assert.Equal((1, 2, 1, timeFrameId), (saved.ExchangeId, saved.MarketId, saved.SymbolId, saved.TimeFrameId));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0), saved.StartDate); // stored as UTC wall-clock time
    }

    [Fact]
    public async Task ASeriesAlreadyConfiguredIsAConflictNotAServerError()
    {
        var client = await AdminAsync();
        (await client.PostAsJsonAsync(AddUrl, Series(symbolId: 2, timeFrameId: 3600), Ct)).EnsureSuccessStatusCode();

        var again = await client.PostAsJsonAsync(AddUrl, Series(symbolId: 2, timeFrameId: 3600), Ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await again.Content.ReadFromJsonAsync<Problem>(Ct);
        Assert.Equal("Configuration already exists.", problem!.Detail);
        Assert.Single(await ListAsync(client), r => r.SymbolId == 2 && r.TimeFrameId == 3600);
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task AnInvalidRequestIsBadRequestWithAReasonAndNothingIsSaved(string case_, object body)
    {
        var client = await AdminAsync();
        var before = (await ListAsync(client)).Length;

        var response = await client.PostAsJsonAsync(AddUrl, body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{case_}: {(int)response.StatusCode} {text}");
        Assert.DoesNotContain("   at ", text);                    // no stack trace in the answer
        Assert.Equal(before, (await ListAsync(client)).Length);   // and no half-saved row
    }

    public static TheoryData<string, object> InvalidRequests() => new()
    {
        { "exchange missing", Series(1, 900, exchangeId: 0) },
        { "market missing", Series(1, 900, marketId: 0) },
        { "symbol missing", Series(0, 900) },
        { "time frame missing", Series(1, 0) },
        { "unknown exchange", Series(1, 900, exchangeId: 99) },
        { "unknown symbol", Series(999, 900) },
        { "unknown time frame", Series(1, 12345) },
        { "no start date", Series(1, 900, startDate: null) },
        { "start date in the future", Series(1, 900, startDate: "2999-01-01T00:00:00") },
    };
}
