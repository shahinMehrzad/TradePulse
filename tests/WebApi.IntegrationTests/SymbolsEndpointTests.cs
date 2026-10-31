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
    private record SymbolMarketDto(int ListingId, string Exchange, string Market, string ExchangeSymbol);
    private record Item(int Id, string Name, string? Description, SymbolMarketDto[] Markets);
    private record DetailMarket(int ListingId, int ExchangeId, string Exchange, int MarketId, string Market, string ExchangeSymbol, int SeriesCount, int SettingsCount);
    private record Detail(int Id, string Name, string? Description, DetailMarket[] Markets);
    private record SymbolPage(Item[] Items, int PageNumber, int PageSize, int TotalItems, int TotalPages);

    private static async Task<SymbolPage> PageAsync(HttpClient client, string query = "") =>
        (await client.GetFromJsonAsync<SymbolPage>($"{SymbolsUrl}{query}", Ct))!;

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
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(SymbolsUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"{SymbolsUrl}/1", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(SymbolsUrl, new { id = 1, description = "x" }, Ct)).StatusCode);
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

    [Fact]
    public async Task TheSymbolsArePagedByNameWithTheMarketsEachIsListedOn()
    {
        var client = await AdminAsync();

        var first = await PageAsync(client, "?pageSize=5&pageNumber=1");
        var second = await PageAsync(client, "?pageSize=5&pageNumber=2");

        Assert.Equal((5, 5, 1), (first.Items.Length, first.PageSize, first.PageNumber));
        Assert.True(first.TotalItems >= 14);
        Assert.Equal((int)Math.Ceiling(first.TotalItems / 5.0), first.TotalPages);
        Assert.Equal(first.Items.Select(i => i.Name).Order(StringComparer.Ordinal), first.Items.Select(i => i.Name));
        Assert.Empty(first.Items.Select(i => i.Id).Intersect(second.Items.Select(i => i.Id)));
        // ADA/USDT is the first name: listed on Binance Futures and (by the fixture) Binance Spot.
        var ada = Assert.Single(first.Items, i => i.Name == "ADA/USDT");
        Assert.Equal([("Binance", "Futures", "ADAUSDT"), ("Binance", "Spot", "ADAUSDT")],
            ada.Markets.Select(m => (m.Exchange, m.Market, m.ExchangeSymbol)).OrderBy(m => m.Item2, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("btc", "BTC/USDT")]
    [InlineData("  BtC/usdt ", "BTC/USDT")]
    [InlineData("xauusdt", "XAU/USDT")]          // the exchange's name finds the symbol
    public async Task TheSymbolsCanBeSearchedByNameOrByTheExchangesName(string keyword, string expected)
    {
        var client = await AdminAsync();

        var page = await PageAsync(client, $"?keyword={Uri.EscapeDataString(keyword)}");

        Assert.Contains(page.Items, i => i.Name == expected);
        Assert.Equal(page.Items.Length, page.TotalItems);
    }

    [Fact]
    public async Task ASearchAlsoLooksAtTheDescriptionAndNothingMatchingIsAnEmptyPage()
    {
        var client = await AdminAsync();
        var id = await AddSymbolAsync(client, "DESCSEARCH/USDT", "the quick brown fox");

        var byDescription = await PageAsync(client, "?keyword=BROWN%20fox");
        var none = await PageAsync(client, "?keyword=nothing-like-this");

        Assert.Equal(id, Assert.Single(byDescription.Items).Id);
        Assert.Equal((0, 0, 0), (none.Items.Length, none.TotalItems, none.TotalPages));
    }

    [Theory]
    [InlineData("?pageNumber=0")]
    [InlineData("?pageSize=0")]
    [InlineData("?pageSize=101")]
    public async Task APageThatCannotExistIsABadRequest(string query)
    {
        var client = await AdminAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{SymbolsUrl}{query}", Ct)).StatusCode);
    }

    [Fact]
    public async Task TheDescriptionCanBeChangedAndClearedButNeverTheName()
    {
        var client = await AdminAsync();
        var id = await AddSymbolAsync(client, "EDITME/USDT", "first");

        var changed = await client.PutAsJsonAsync(SymbolsUrl, new { id, name = "OTHER/USDT", description = "  second  " }, Ct);
        var afterChange = Assert.Single((await PageAsync(client, "?keyword=editme")).Items);
        await client.PutAsJsonAsync(SymbolsUrl, new { id, description = "   " }, Ct);
        var afterClear = Assert.Single((await PageAsync(client, "?keyword=editme")).Items);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(("EDITME/USDT", "second"), (afterChange.Name, afterChange.Description));
        Assert.Null(afterClear.Description);
        var entries = await EntriesOfAsync(client, "Symbol", id);
        Assert.Equal(["Added", "Updated", "Updated"], entries.Select(e => e.Action));
    }

    [Fact]
    public async Task ChangingTheDescriptionToWhatItAlreadyIsIsNotLogged()
    {
        var client = await AdminAsync();
        var id = await AddSymbolAsync(client, "SAMEDESC/USDT", "same");

        var response = await client.PutAsJsonAsync(SymbolsUrl, new { id, description = "same" }, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["Added"], (await EntriesOfAsync(client, "Symbol", id)).Select(e => e.Action));
    }

    [Fact]
    public async Task AnEditThatCannotBeDoneIsRefused()
    {
        var client = await AdminAsync();
        var id = await AddSymbolAsync(client, "TOOLONG/USDT", "ok");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(SymbolsUrl, new { id = 0, description = "x" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(SymbolsUrl, new { id, description = new string('x', 101) }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(SymbolsUrl, new { id = 999_999, description = "x" }, Ct)).StatusCode);
        Assert.Equal("ok", Assert.Single((await PageAsync(client, "?keyword=toolong")).Items).Description);
    }

    [Fact]
    public async Task OneSymbolShowsItsMarketsAndWhatUsesEachListing()
    {
        var client = await AdminAsync();
        api.ExchangeCatalog.Trading.Add("DETAILSUSDT");
        var symbol = await AddSymbolAsync(client, "DETAILS/USDT", "two markets");
        var futures = (await (await PostListingAsync(client, symbol, "DETAILSUSDT", Futures)).Content.ReadFromJsonAsync<Created>(Ct))!.Id;
        var spot = (await (await PostListingAsync(client, symbol, "DETAILSUSDT", Spot)).Content.ReadFromJsonAsync<Created>(Ct))!.Id;
        (await client.PostAsJsonAsync("/api/admin/configurations/add-config",
            new { symbolListingId = futures, timeFrameId = 3600, startDate = "2020-01-01T00:00:00Z" }, Ct)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/admin/configurations/add-symbol-data",
            new { symbolListingId = futures, collectFundingRate = true, fundingStartDate = "2020-01-01T00:00:00Z", collectLiquidation = true }, Ct)).EnsureSuccessStatusCode();

        var detail = (await client.GetFromJsonAsync<Detail>($"{SymbolsUrl}/{symbol}", Ct))!;

        Assert.Equal(("DETAILS/USDT", "two markets"), (detail.Name, detail.Description));
        Assert.Equal(
            [(futures, "Binance", "Futures", "DETAILSUSDT", 1, 1), (spot, "Binance", "Spot", "DETAILSUSDT", 0, 0)],
            detail.Markets.Select(m => (m.ListingId, m.Exchange, m.Market, m.ExchangeSymbol, m.SeriesCount, m.SettingsCount)).OrderByDescending(m => m.ListingId == futures));
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"{ListingsUrl}/{futures}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{ListingsUrl}/{spot}", Ct)).StatusCode);
    }

    [Fact]
    public async Task ASymbolListedNowhereHasNoMarketsAndAMissingOneIsNotFound()
    {
        var client = await AdminAsync();
        var symbol = await AddSymbolAsync(client, "NOWHERE/USDT");

        var detail = (await client.GetFromJsonAsync<Detail>($"{SymbolsUrl}/{symbol}", Ct))!;

        Assert.Empty(detail.Markets);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"{SymbolsUrl}/999999", Ct)).StatusCode);
    }
}
