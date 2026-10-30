using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>Funding rate and liquidation are chosen per symbol (not per time frame): one row per exchange + market + symbol.</summary>
public class SymbolDataConfigurationEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string ListUrl = "/api/admin/configurations/symbol-data";
    private const string AddUrl = "/api/admin/configurations/add-symbol-data";
    private const string UpdateUrl = "/api/admin/configurations/update-symbol-data";
    private static string DeleteUrl(int id) => $"/api/admin/configurations/delete-symbol-data/{id}";

    private const int Binance = 1;
    private const int Spot = 1;
    private const int Futures = 2;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Row(int Id, int ExchangeId, int MarketId, int SymbolId, bool CollectFundingRate, DateTime? FundingStartDate, bool CollectLiquidation);
    private record Created(int Id);
    private record Problem(int Status, string? Title, string? Detail);

    // The database is shared by the tests of this class and a symbol can have one row, so every test that saves takes a symbol
    // of its own (the lookup holds 14). Tests that are refused before anything is saved all use symbol 1.
    private static int _nextSymbol;
    private static int NewSymbol()
    {
        var symbol = Interlocked.Increment(ref _nextSymbol);
        Assert.True(symbol <= 14, "this class has run out of symbols; give the lookup more or share one");
        return symbol;
    }

    private static object Config(int symbolId, bool funding = true, bool liquidation = true, string? fundingStartDate = "2020-01-01T00:00:00Z",
        int exchangeId = Binance, int marketId = Futures) =>
        new { exchangeId, marketId, symbolId, collectFundingRate = funding, fundingStartDate, collectLiquidation = liquidation };

    private static object Edit(int id, int symbolId, bool funding = true, bool liquidation = true, string? fundingStartDate = "2021-06-01T00:00:00Z") =>
        new { id, exchangeId = Binance, marketId = Futures, symbolId, collectFundingRate = funding, fundingStartDate, collectLiquidation = liquidation };

    private async Task<HttpClient> AdminAsync()
    {
        var client = api.CreateClient();
        var response = await api.AdminLoginAsync(client, Ct);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static async Task<Row[]> ListAsync(HttpClient client) => (await client.GetFromJsonAsync<Row[]>(ListUrl, Ct))!;

    private static async Task<int> AddAsync(HttpClient client, object body)
    {
        var response = await client.PostAsJsonAsync(AddUrl, body, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
    }

    [Fact]
    public async Task WithoutATokenEveryEndpointIsUnauthorized()
    {
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ListUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(AddUrl, Config(1), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(UpdateUrl, Edit(1, 1), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync(DeleteUrl(1), Ct)).StatusCode);
    }

    [Fact]
    public async Task ASymbolCanAskForBothAndTheChoiceIsStored()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();

        var response = await client.PostAsJsonAsync(AddUrl, Config(symbol), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
        Assert.True(id > 0);
        var saved = Assert.Single(await ListAsync(client), r => r.Id == id);
        Assert.Equal((Binance, Futures, symbol, true, true), (saved.ExchangeId, saved.MarketId, saved.SymbolId, saved.CollectFundingRate, saved.CollectLiquidation));
        Assert.Equal(new DateTime(2020, 1, 1), saved.FundingStartDate);   // stored as UTC wall-clock time, the "Z" accepted
    }

    [Fact]
    public async Task LiquidationAloneNeedsNoDateAndAnyDateSentIsNotStored()
    {
        var client = await AdminAsync();

        var id = await AddAsync(client, Config(NewSymbol(), funding: false, liquidation: true, fundingStartDate: "2020-01-01T00:00:00Z"));

        var saved = Assert.Single(await ListAsync(client), r => r.Id == id);
        Assert.Equal((false, true, (DateTime?)null), (saved.CollectFundingRate, saved.CollectLiquidation, saved.FundingStartDate));
    }

    [Fact]
    public async Task FundingRateAloneIsStoredWithItsDate()
    {
        var client = await AdminAsync();

        var id = await AddAsync(client, Config(NewSymbol(), funding: true, liquidation: false));

        var saved = Assert.Single(await ListAsync(client), r => r.Id == id);
        Assert.Equal((true, false, (DateTime?)new DateTime(2020, 1, 1)), (saved.CollectFundingRate, saved.CollectLiquidation, saved.FundingStartDate));
    }

    [Fact]
    public async Task ASymbolThatAlreadyHasARowIsAConflictNotAServerError()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        await AddAsync(client, Config(symbol));

        var again = await client.PostAsJsonAsync(AddUrl, Config(symbol, funding: false), Ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("Configuration already exists.", (await again.Content.ReadFromJsonAsync<Problem>(Ct))!.Detail);
        Assert.Single(await ListAsync(client), r => r.SymbolId == symbol);
    }

    [Theory]
    [MemberData(nameof(RefusedRequests))]
    public async Task ARequestTheMarketCannotServeIsBadRequestWithTheReasonAndNothingIsSaved(string case_, object body, string reason)
    {
        var client = await AdminAsync();
        var before = (await ListAsync(client)).Length;

        var response = await client.PostAsJsonAsync(AddUrl, body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{case_}: {(int)response.StatusCode} {text}");
        Assert.Contains(reason, (await response.Content.ReadFromJsonAsync<Problem>(Ct))!.Detail);
        Assert.DoesNotContain("   at ", text);
        Assert.Equal(before, (await ListAsync(client)).Length);
    }

    public static TheoryData<string, object, string> RefusedRequests() => new()
    {
        { "funding rate on Spot", Config(1, funding: true, liquidation: false, marketId: Spot), "FundingRate is not available on Binance Spot" },
        { "liquidation on Spot", Config(1, funding: false, liquidation: true, marketId: Spot), "Liquidation is not available on Binance Spot" },
        { "nothing chosen", Config(1, funding: false, liquidation: false), "Choose funding rate, liquidation or both" },
        { "funding rate without a date", Config(1, funding: true, liquidation: false, fundingStartDate: null), "FundingStartDate is required" },
        { "funding rate with a date in the future", Config(1, funding: true, liquidation: false, fundingStartDate: "2999-01-01T00:00:00Z"), "must be in the past" },
        { "exchange missing", Config(1, exchangeId: 0), "required" },
        { "market missing", Config(1, marketId: 0), "required" },
        { "symbol missing", Config(0), "required" },
        { "unknown exchange", Config(1, exchangeId: 99), "unknown exchange" },
        { "unknown market", Config(1, marketId: 99), "unknown market" },
        { "unknown symbol", Config(999), "Unknown exchange, market or symbol" },
    };

    [Fact]
    public async Task UpdateChangesTheRowInPlace()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        var id = await AddAsync(client, Config(symbol, funding: true, liquidation: false));
        var rowsBefore = (await ListAsync(client)).Length;

        var response = await client.PutAsJsonAsync(UpdateUrl, Edit(id, symbol, funding: false, liquidation: true), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = (await response.Content.ReadFromJsonAsync<Row>(Ct))!;
        var rows = await ListAsync(client);
        var saved = Assert.Single(rows, r => r.Id == id);
        Assert.Equal(returned, saved);
        Assert.Equal((false, true, (DateTime?)null), (saved.CollectFundingRate, saved.CollectLiquidation, saved.FundingStartDate));
        Assert.Equal(rowsBefore, rows.Length);   // an edit must never add a row
    }

    [Fact]
    public async Task UpdatingToASymbolThatAlreadyHasARowIsAConflictAndChangesNothing()
    {
        var client = await AdminAsync();
        var first = NewSymbol();
        var second = NewSymbol();
        var id = await AddAsync(client, Config(first));
        await AddAsync(client, Config(second));

        var response = await client.PutAsJsonAsync(UpdateUrl, Edit(id, second), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(first, Assert.Single(await ListAsync(client), r => r.Id == id).SymbolId);
    }

    [Fact]
    public async Task UpdatingARowThatDoesNotExistIsNotFound()
    {
        var client = await AdminAsync();

        var response = await client.PutAsJsonAsync(UpdateUrl, Edit(999_999, 1), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AnInvalidEditIsBadRequestAndTheRowKeepsItsValues()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        var id = await AddAsync(client, Config(symbol));
        var before = Assert.Single(await ListAsync(client), r => r.Id == id);

        var invalid = new (string Case, object Body)[]
        {
            ("no id", Edit(0, symbol)),
            ("nothing chosen", Edit(id, symbol, funding: false, liquidation: false)),
            ("funding rate without a date", Edit(id, symbol, fundingStartDate: null)),
            ("funding rate with a date in the future", Edit(id, symbol, fundingStartDate: "2999-01-01T00:00:00Z")),
            ("moved to Spot", new { id, exchangeId = Binance, marketId = Spot, symbolId = symbol, collectFundingRate = true, fundingStartDate = "2021-06-01T00:00:00Z", collectLiquidation = true }),
            ("unknown symbol", Edit(id, 999)),
        };

        foreach (var (case_, body) in invalid)
        {
            var response = await client.PutAsJsonAsync(UpdateUrl, body, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{case_}: {(int)response.StatusCode}");
            Assert.Equal(before, Assert.Single(await ListAsync(client), r => r.Id == id));
        }
    }

    [Fact]
    public async Task DeleteRemovesOnlyThatRowAndAnswersNoContent()
    {
        var client = await AdminAsync();
        var doomed = await AddAsync(client, Config(NewSymbol()));
        var survivor = await AddAsync(client, Config(NewSymbol()));

        var response = await client.DeleteAsync(DeleteUrl(doomed), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var rows = await ListAsync(client);
        Assert.DoesNotContain(rows, r => r.Id == doomed);
        Assert.Contains(rows, r => r.Id == survivor);
    }

    [Fact]
    public async Task DeletingARowThatIsGoneIsNotFoundAndIdZeroIsBadRequest()
    {
        var client = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync(DeleteUrl(999_999), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync(DeleteUrl(0), Ct)).StatusCode);
    }

    [Fact]
    public async Task ADeletedSymbolCanBeAddedAgain()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        var id = await AddAsync(client, Config(symbol));
        (await client.DeleteAsync(DeleteUrl(id), Ct)).EnsureSuccessStatusCode();

        // The unique index no longer holds the symbol, so it is not a conflict any more.
        Assert.NotEqual(id, await AddAsync(client, Config(symbol)));
    }
}
