using System.Net.Http.Headers;
using System.Net.Http.Json;
using WebRazor.Api;

namespace WebApi.IntegrationTests;

/// <summary>
/// The query the panel's Series page builds (<see cref="SeriesListQuery"/>, compiled into this project), first on its own and then
/// against the real endpoint: the sorts, filters and page sizes the page offers must be ones the API takes, and must do what they say.
/// </summary>
public class SeriesListQueryTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Url = "/api/admin/configurations/candle-worker/odata";
    private const int Futures = 2;
    private const int Spot = 1;

    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Row(int Id, string Symbol, int IntervalSeconds, bool CollectOpenInterest, bool CollectMarketStatistics);
    private record Page(int Count, Row[] Value);

    private static SeriesListQuery Query(
        int? page = null, int? size = null, string? keyword = null, string? sort = null, string? dir = null,
        int? exchange = null, int? market = null, int? timeFrame = null, string? oi = null, string? stats = null) =>
        SeriesListQuery.From(page, size, keyword, sort, dir, exchange, market, timeFrame, oi, stats);

    // ---- on its own -------------------------------------------------------------------------------------------------------

    [Fact]
    public void AnUntouchedListIsTheUsualOrderWithTheDefaultPageAndACleanAddress()
    {
        var query = Query();

        Assert.Equal("$orderby=symbol%2CintervalSeconds%2Cid&$top=10&$skip=0", query.ToODataQuery());
        Assert.Empty(query.RouteValues());
        Assert.False(query.IsFiltered);
    }

    [Theory]
    [InlineData("nonsense")]
    [InlineData("id; drop table")]
    [InlineData("")]
    public void ASortThatIsNotOnTheListIsTheUsualOrder(string sort)
    {
        var query = Query(sort: sort, dir: "desc");

        Assert.Null(query.SortBy);
        Assert.False(query.Descending);   // a direction without a column means nothing
        Assert.StartsWith("$orderby=symbol%2CintervalSeconds%2Cid", query.ToODataQuery());
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(0, 10)]
    [InlineData(7, 10)]
    [InlineData(101, 10)]
    [InlineData(25, 25)]
    [InlineData(100, 100)]
    public void OnlyThePageSizesTheApiOffersAreKept(int? asked, int expected) =>
        Assert.Equal(expected, Query(size: asked).PageSize);

    [Theory]
    [InlineData(null, 1)]
    [InlineData(-3, 1)]
    [InlineData(0, 1)]
    [InlineData(4, 4)]
    public void ThePageNumberNeverGoesBelowTheFirst(int? asked, int expected) =>
        Assert.Equal(expected, Query(page: asked).PageNumber);

    [Fact]
    public void ASortedColumnComesFirstAndTheUsualOrderBreaksTies()
    {
        Assert.StartsWith("$orderby=intervalSeconds%20desc%2Csymbol%2Cid", Query(sort: "timeframe", dir: "desc").ToODataQuery());

        // The sorted column is not asked for twice when it is one of the tie breakers already.
        Assert.StartsWith("$orderby=symbol%20desc%2CintervalSeconds%2Cid", Query(sort: "symbol", dir: "desc").ToODataQuery());
    }

    [Fact]
    public void AClickOnAHeaderSortsAscendingThenTurnsItRoundAndStartsAgainAtTheFirstPage()
    {
        var onPageThree = Query(page: 3, sort: "market");

        var turned = onPageThree.SortedBy("market");
        var turnedBack = turned.SortedBy("market");
        var other = turned.SortedBy("from");

        Assert.Equal(("market", true, 1), (turned.SortBy, turned.Descending, turned.PageNumber));
        Assert.Equal(("market", false), (turnedBack.SortBy, turnedBack.Descending));
        Assert.Equal(("from", false), (other.SortBy, other.Descending));
    }

    [Fact]
    public void FiltersBecomeOneFilterAndOnlyTheDifferencesFromTheDefaultsAreInTheAddress()
    {
        var query = Query(keyword: " BTC ", exchange: 1, market: 2, timeFrame: 3600, oi: "on", stats: "off");

        Assert.Equal(
            "(contains(tolower(exchange),'btc') or contains(tolower(market),'btc') or contains(tolower(symbol),'btc') or contains(tolower(symbolDescription),'btc'))"
            + " and exchangeId eq 1 and marketId eq 2 and timeFrameId eq 3600 and collectOpenInterest eq true and collectMarketStatistics eq false",
            query.Filter());
        Assert.Equal(
            new Dictionary<string, string>
            {
                ["keyword"] = "BTC", ["exchange"] = "1", ["market"] = "2", ["timeframe"] = "3600", ["oi"] = "on", ["stats"] = "off",
            },
            query.RouteValues());
    }

    [Fact]
    public void AQuoteInTheSearchIsWrittenTwiceSoItCannotLeaveTheText() =>
        Assert.Contains("'o''brien'", Query(keyword: "O'Brien").Filter());

    [Fact]
    public void AnythingElseThanOnOrOffIsNoFilter()
    {
        Assert.Equal(FlagFilter.Any, SeriesListQuery.ParseFlag("yes"));
        Assert.Equal(FlagFilter.Any, SeriesListQuery.ParseFlag(null));
        Assert.Equal(FlagFilter.On, SeriesListQuery.ParseFlag(" ON "));
    }

    // ---- against the real endpoint ----------------------------------------------------------------------------------------
    // Five series: BTCUSDT futures at 15m, 1h (open interest and statistics) and 4h (open interest), SOLUSDT futures at 1h, ETHUSDT spot at 1h.

    private async Task<HttpClient> SeededAdminAsync()
    {
        var client = api.CreateClient();
        var response = await api.AdminLoginAsync(client, Ct);
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await response.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken);

        await SeedLock.WaitAsync(Ct);
        try
        {
            if (!_seeded)
            {
                await AddAsync(client, symbolId: 1, Futures, timeFrameId: 900);
                await AddAsync(client, symbolId: 1, Futures, timeFrameId: 3600, openInterest: true, statistics: true);
                await AddAsync(client, symbolId: 1, Futures, timeFrameId: 14400, openInterest: true);
                await AddAsync(client, symbolId: 4, Futures, timeFrameId: 3600);
                await AddAsync(client, symbolId: 3, Spot, timeFrameId: 3600);
                _seeded = true;
            }
        }
        finally
        {
            SeedLock.Release();
        }

        return client;
    }

    private static async Task AddAsync(HttpClient client, int symbolId, int marketId, int timeFrameId, bool openInterest = false, bool statistics = false) =>
        (await client.PostAsJsonAsync("/api/admin/configurations/add-config",
            new
            {
                symbolListingId = ListingIds.Of(1, marketId, symbolId), timeFrameId, startDate = "2020-01-01T00:00:00Z",
                collectOpenInterest = openInterest, collectMarketStatistics = statistics,
            }, Ct)).EnsureSuccessStatusCode();

    private static async Task<Page> AskAsync(HttpClient client, SeriesListQuery query)
    {
        var response = await client.GetAsync($"{Url}?{query.ToODataQuery()}", Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<Page>(Ct))!;
    }

    [Fact]
    public async Task TheUsualListHasEverySeriesInTheUsualOrder()
    {
        var page = await AskAsync(await SeededAdminAsync(), Query());

        Assert.Equal(5, page.Count);
        // by symbol, then by time frame
        Assert.Equal([900, 3600, 14400], page.Value.Where(r => r.Symbol.StartsWith("BTC")).Select(r => r.IntervalSeconds));
    }

    [Theory]
    [InlineData(Futures, 4)]
    [InlineData(Spot, 1)]
    [InlineData(99, 0)]
    public async Task TheMarketFilterKeepsOnlyThatMarket(int market, int expected) =>
        Assert.Equal(expected, (await AskAsync(await SeededAdminAsync(), Query(market: market))).Count);

    [Theory]
    [InlineData(1, 5)]
    [InlineData(99, 0)]
    public async Task TheExchangeFilterKeepsOnlyThatExchange(int exchange, int expected) =>
        Assert.Equal(expected, (await AskAsync(await SeededAdminAsync(), Query(exchange: exchange))).Count);

    [Theory]
    [InlineData(3600, 3)]
    [InlineData(900, 1)]
    public async Task TheTimeFrameFilterKeepsOnlyThatTimeFrame(int timeFrame, int expected) =>
        Assert.Equal(expected, (await AskAsync(await SeededAdminAsync(), Query(timeFrame: timeFrame))).Count);

    [Theory]
    [InlineData("on", 2)]
    [InlineData("off", 3)]
    [InlineData("any", 5)]
    public async Task TheOpenInterestFilterSplitsTheListByTheFlag(string flag, int expected)
    {
        var page = await AskAsync(await SeededAdminAsync(), Query(oi: flag));

        Assert.Equal(expected, page.Count);
        if (flag != "any")
            Assert.All(page.Value, r => Assert.Equal(flag == "on", r.CollectOpenInterest));
    }

    [Theory]
    [InlineData("on", 1)]
    [InlineData("off", 4)]
    public async Task TheStatisticsFilterSplitsTheListByTheFlag(string flag, int expected)
    {
        var page = await AskAsync(await SeededAdminAsync(), Query(stats: flag));

        Assert.Equal(expected, page.Count);
        Assert.All(page.Value, r => Assert.Equal(flag == "on", r.CollectMarketStatistics));
    }

    [Fact]
    public async Task TheSearchAndTheFiltersNarrowTheListTogether()
    {
        var page = await AskAsync(await SeededAdminAsync(), Query(keyword: "btc", oi: "on", market: Futures));

        Assert.Equal(2, page.Count);
        Assert.All(page.Value, r => Assert.StartsWith("BTC", r.Symbol));
    }

    [Theory]
    [InlineData("asc", 900)]
    [InlineData("desc", 14400)]
    public async Task SortingByTimeFrameIsByItsLengthOfTime(string dir, int firstInterval)
    {
        var page = await AskAsync(await SeededAdminAsync(), Query(sort: "timeframe", dir: dir));

        Assert.Equal(firstInterval, page.Value[0].IntervalSeconds);
    }

    [Fact]
    public async Task SortingBySymbolDescendingPutsTheLastSymbolFirst()
    {
        var page = await AskAsync(await SeededAdminAsync(), Query(sort: "symbol", dir: "desc"));

        var symbols = page.Value.Select(r => r.Symbol).ToArray();
        Assert.Equal(symbols.OrderByDescending(s => s, StringComparer.Ordinal), symbols);
    }

    [Theory]
    [InlineData("oi")]
    [InlineData("stats")]
    [InlineData("from")]
    [InlineData("exchange")]
    [InlineData("market")]
    public async Task EveryColumnThePageCanSortByIsTakenByTheApiInBothDirections(string sort)
    {
        var client = await SeededAdminAsync();

        Assert.Equal(5, (await AskAsync(client, Query(sort: sort))).Value.Length);
        Assert.Equal(5, (await AskAsync(client, Query(sort: sort, dir: "desc"))).Value.Length);
    }

    [Fact]
    public async Task ABooleanColumnSortedDescendingPutsTheOnesFirst()
    {
        var page = await AskAsync(await SeededAdminAsync(), Query(sort: "oi", dir: "desc"));

        Assert.True(page.Value[0].CollectOpenInterest);
        Assert.True(page.Value[1].CollectOpenInterest);
        Assert.False(page.Value[2].CollectOpenInterest);
    }

    [Fact]
    public async Task ThePagesOfASortedListNeitherOverlapNorLoseARow()
    {
        var client = await SeededAdminAsync();

        // Pages of two (not an offered size, but the API takes any up to 100): the same five rows over three pages.
        var small = Query(sort: "oi") with { PageSize = 2 };
        var paged = new List<int>();
        foreach (var number in new[] { 1, 2, 3 })
            paged.AddRange((await AskAsync(client, small.OnPage(number))).Value.Select(r => r.Id));

        Assert.Equal(5, paged.Count);
        Assert.Equal(5, paged.Distinct().Count());
    }
}
