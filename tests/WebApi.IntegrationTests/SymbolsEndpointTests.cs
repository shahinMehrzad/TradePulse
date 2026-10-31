using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>The symbols and where each is listed: managed by the admin, checked against what the exchange lists (a fake one here).</summary>
public class SymbolsEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string SymbolsUrl = "/api/admin/symbols";
    private const string ListingsUrl = "/api/admin/symbols/listings";
    private const string AuditUrl = "/api/admin/audit-log";

    private const int Binance = 1;
    private const int Spot = 1;
    private const int Futures = 2;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Created(int Id);
    private record Lookup(int Id, string Name, string? Description);
    private record Listing(int Id, int ExchangeId, string Exchange, int MarketId, string Market, int SymbolId, string Symbol, string ExchangeSymbol);
    private record Problem(int Status, string? Title, string? Detail);
    private record Entry(string UserName, string Action, string EntityType, int EntityId, string Summary);
    private record Page(Entry[] Value);

    private async Task<HttpClient> AdminAsync()
    {
        var client = api.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { Email = ApiFixture.AdminEmail, Username = "", Password = ApiFixture.AdminPassword }, Ct);
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await response.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken);
        return client;
    }

    private static async Task<int> AddSymbolAsync(HttpClient client, string name, string? description = null)
    {
        var response = await client.PostAsJsonAsync(SymbolsUrl, new { name, description }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
    }

    private static Task<HttpResponseMessage> PostListingAsync(HttpClient client, int symbolId, string exchangeSymbol, int market = Futures, int exchange = Binance) =>
        client.PostAsJsonAsync(ListingsUrl, new { exchangeId = exchange, marketId = market, symbolId, exchangeSymbol }, Ct);

    private static async Task<Listing[]> ListingsAsync(HttpClient client) => (await client.GetFromJsonAsync<Listing[]>(ListingsUrl, Ct))!;

    private static async Task<Problem> ProblemOfAsync(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<Problem>(Ct))!;

    private static async Task<Entry[]> EntriesOfAsync(HttpClient client, string entityType, int entityId)
    {
        var filter = Uri.EscapeDataString($"entityType eq '{entityType}' and entityId eq {entityId}");
        return (await client.GetFromJsonAsync<Page>($"{AuditUrl}?$filter={filter}&$orderby=occurredAt,id", Ct))!.Value;
    }

    [Fact]
    public async Task WithoutATokenEveryEndpointIsUnauthorized()
    {
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(SymbolsUrl, new { name = "AAA/USDT" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"{SymbolsUrl}/1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ListingsUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(ListingsUrl, new { }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"{ListingsUrl}/1", Ct)).StatusCode);
    }

    [Fact]
    public async Task ASymbolIsStoredUpperCaseTrimmedAndShowsInTheLookupAndTheAuditLog()
    {
        var client = await AdminAsync();

        var id = await AddSymbolAsync(client, "  wif/usdt ", "  dog hat  ");

        var symbols = (await client.GetFromJsonAsync<Lookup[]>("/api/lookups/symbols?keyword=wif", Ct))!;
        Assert.Equal(new Lookup(id, "WIF/USDT", "dog hat"), Assert.Single(symbols));
        var entry = Assert.Single(await EntriesOfAsync(client, "Symbol", id));
        Assert.Equal(("Added", "WIF/USDT"), (entry.Action, entry.Summary));
    }

    [Fact]
    public async Task ASymbolNameTakenAlreadyIsAConflict()
    {
        var client = await AdminAsync();
        await AddSymbolAsync(client, "TWICE/USDT");

        var response = await client.PostAsJsonAsync(SymbolsUrl, new { name = "twice/usdt" }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("BTCUSDT")]
    [InlineData("BTC/")]
    [InlineData("/USDT")]
    [InlineData("BTC/USDT/X")]
    [InlineData("BT C/USDT")]
    [InlineData("BTC;/USDT")]
    [InlineData("ÉTH/USDT")]
    public async Task ANameThatIsNotBaseSlashQuoteIsABadRequest(string? name)
    {
        var client = await AdminAsync();

        var response = await client.PostAsJsonAsync(SymbolsUrl, new { name }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ASymbolCanBeListedUnderTheNameTheExchangeSpellsIt()
    {
        var client = await AdminAsync();
        var symbol = await AddSymbolAsync(client, "1000PEPE/USDT");

        var response = await PostListingAsync(client, symbol, "1000pepeusdt");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
        var listing = Assert.Single(await ListingsAsync(client), l => l.Id == id);
        Assert.Equal(("Binance", "Futures", "1000PEPE/USDT", "1000PEPEUSDT"), (listing.Exchange, listing.Market, listing.Symbol, listing.ExchangeSymbol));
        var entry = Assert.Single(await EntriesOfAsync(client, "SymbolListing", id));
        Assert.Equal(("Added", "Binance / Futures / 1000PEPE/USDT = 1000PEPEUSDT"), (entry.Action, entry.Summary));
    }

    [Fact]
    public async Task TheSeededSymbolsAreListedOnBinanceFutures()
    {
        var client = await AdminAsync();

        var listings = await ListingsAsync(client);

        var btc = Assert.Single(listings, l => l.Symbol == "BTC/USDT" && l.MarketId == Futures);
        Assert.Equal(("Binance", "BTCUSDT"), (btc.Exchange, btc.ExchangeSymbol));
        Assert.True(listings.Count(l => l.MarketId == Futures) >= 14);
        Assert.True(listings.Count(l => l.MarketId == Spot) >= 14);   // the fixture lists them on Spot too
    }

    [Fact]
    public async Task ANameTheExchangeDoesNotTradeIsRefusedAndNothingIsSaved()
    {
        var client = await AdminAsync();
        var symbol = await AddSymbolAsync(client, "NOPE/USDT");

        var response = await PostListingAsync(client, symbol, "NOPEUSDT");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("NOPEUSDT", (await ProblemOfAsync(response)).Detail);
        Assert.DoesNotContain(await ListingsAsync(client), l => l.SymbolId == symbol);
    }

    [Fact]
    public async Task WhenTheExchangeCannotBeAskedNothingIsSavedAndTheAnswerSaysTryAgain()
    {
        var client = await AdminAsync();
        var symbol = await AddSymbolAsync(client, "SOLDOWN/USDT");
        api.ExchangeCatalog.Down = true;
        try
        {
            var response = await PostListingAsync(client, symbol, "SOLDOWNUSDT", market: Spot);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain(await ListingsAsync(client), l => l.SymbolId == symbol);
        }
        finally
        {
            api.ExchangeCatalog.Down = false;
        }
    }

    [Fact]
    public async Task ANameThatCannotBeInATableNameIsRefusedBeforeTheExchangeIsAsked()
    {
        var client = await AdminAsync();
        var symbol = await AddSymbolAsync(client, "EVIL/USDT");
        var calls = api.ExchangeCatalog.Calls;

        var response = await PostListingAsync(client, symbol, "BTCUSDT; DROP TABLE x");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(calls, api.ExchangeCatalog.Calls);
    }

    [Fact]
    public async Task ASymbolIsListedOncePerMarketAndNamesThatShareTableNamesAreRefused()
    {
        var client = await AdminAsync();
        var eth = await AddSymbolAsync(client, "QQQ/USDT");
        var other = await AddSymbolAsync(client, "QQQDASH/USDT");
        api.ExchangeCatalog.Trading.Add("QQQUSDT");
        api.ExchangeCatalog.Trading.Add("QQQ-USDT");
        Assert.Equal(HttpStatusCode.Created, (await PostListingAsync(client, eth, "QQQUSDT", market: Spot)).StatusCode);

        // The same name again, in another case; the same table names behind another spelling; the same symbol under another name.
        Assert.Equal(HttpStatusCode.Conflict, (await PostListingAsync(client, other, "qqqusdt", market: Spot)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostListingAsync(client, other, "QQQ-USDT", market: Spot)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await PostListingAsync(client, eth, "1000PEPEUSDT", market: Spot)).StatusCode);
        // Another market has its own tables, so the same table-name part is fine there.
        api.ExchangeCatalog.Trading.Add("ZZZUSDT");
        api.ExchangeCatalog.Trading.Add("ZZZ-USDT");
        Assert.Equal(HttpStatusCode.Created, (await PostListingAsync(client, other, "ZZZUSDT", market: Spot)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostListingAsync(client, other, "ZZZ-USDT", market: Futures)).StatusCode);
        // On Futures, ETHUSDT was seeded, so a second spelling of it is refused there as well.
        Assert.Equal(HttpStatusCode.Conflict, (await PostListingAsync(client, other, "ETH-USDT", market: Futures)).StatusCode);
    }

    [Theory]
    [InlineData(0, 2, 1)]
    [InlineData(99, 2, 1)]
    [InlineData(1, 99, 1)]
    [InlineData(1, 2, 9999)]
    public async Task AnUnknownExchangeMarketOrSymbolIsABadRequest(int exchange, int market, int symbol)
    {
        var client = await AdminAsync();

        var response = await PostListingAsync(client, symbol, "BTCUSDT", market, exchange);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AListingASettingStillUsesCannotBeRemovedAndAFreeOneCan()
    {
        var client = await AdminAsync();
        var btcFutures = Assert.Single(await ListingsAsync(client), l => l.Symbol == "BTC/USDT" && l.MarketId == Futures);
        var config = await client.PostAsJsonAsync("/api/admin/configurations/add-config",
            new { symbolListingId = btcFutures.Id, timeFrameId = 14400, startDate = "2020-01-01T00:00:00Z" }, Ct);
        config.EnsureSuccessStatusCode();

        var inUse = await client.DeleteAsync($"{ListingsUrl}/{btcFutures.Id}", Ct);

        Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
        Assert.Contains(await ListingsAsync(client), l => l.Id == btcFutures.Id);

        var symbol = await AddSymbolAsync(client, "TEMP1/USDT");
        api.ExchangeCatalog.Trading.Add("TEMP1USDT");
        var listing = (await (await PostListingAsync(client, symbol, "TEMP1USDT")).Content.ReadFromJsonAsync<Created>(Ct))!.Id;

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{ListingsUrl}/{listing}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{ListingsUrl}/{listing}", Ct)).StatusCode);
        Assert.Equal("Removed", Assert.Single(await EntriesOfAsync(client, "SymbolListing", listing), e => e.Action == "Removed").Action);
    }

    [Fact]
    public async Task ASymbolThatIsStillListedCannotBeRemovedAndAFreeOneCan()
    {
        var client = await AdminAsync();
        var listed = await AddSymbolAsync(client, "TEMP2/USDT");
        api.ExchangeCatalog.Trading.Add("TEMP2USDT");
        Assert.Equal(HttpStatusCode.Created, (await PostListingAsync(client, listed, "TEMP2USDT")).StatusCode);
        var free = await AddSymbolAsync(client, "TEMP3/USDT");

        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{SymbolsUrl}/{listed}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{SymbolsUrl}/{free}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{SymbolsUrl}/{free}", Ct)).StatusCode);
    }
}
