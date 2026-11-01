using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>
/// The OData flavour of the candle series list. The same thirteen series as the paging tests: twelve BTCUSDT futures series
/// (one per time frame, so many rows share a symbol) and one ETHUSDT spot series.
/// </summary>
public class CandleWorkerConfigurationODataTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Url = "/api/admin/configurations/candle-worker/odata";
    private static readonly int[] BtcTimeFrames = [1, 60, 180, 300, 900, 1800, 3600, 7200, 14400, 21600, 28800, 43200];

    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Row(int Id, string Exchange, string Market, string Symbol, int IntervalSeconds, bool CollectOpenInterest);
    private record Page(int Count, Row[] Value);

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
                foreach (var timeFrame in BtcTimeFrames)
                    await AddAsync(client, symbolId: 1, marketId: 2, timeFrame);
                await AddAsync(client, symbolId: 3, marketId: 1, timeFrameId: 3600);
                _seeded = true;
            }
        }
        finally
        {
            SeedLock.Release();
        }

        return client;
    }

    private static async Task AddAsync(HttpClient client, int symbolId, int marketId, int timeFrameId) =>
        (await client.PostAsJsonAsync("/api/admin/configurations/add-config",
            new { symbolListingId = ListingIds.Of(1, marketId, symbolId), timeFrameId, startDate = "2020-01-01T00:00:00Z" }, Ct)).EnsureSuccessStatusCode();

    private static async Task<Page> GetPageAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync($"{Url}?{query}", Ct);
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(Ct)}");
        return (await response.Content.ReadFromJsonAsync<Page>(Ct))!;
    }

    [Fact]
    public async Task WithoutATokenTheListIsUnauthorized()
    {
        var response = await api.CreateClient().GetAsync(Url, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithoutOptionsEveryRowUpToTheDefaultPageIsReturned()
    {
        var client = await SeededAdminAsync();

        var page = await GetPageAsync(client, "");

        Assert.Equal((13, 13), (page.Count, page.Value.Length));
    }

    [Fact]
    public async Task TopAndSkipPageTheListAndCountStillReportsTheWholeFilteredSet()
    {
        var client = await SeededAdminAsync();

        var page = await GetPageAsync(client, "$top=5&$skip=10");

        Assert.Equal((13, 3), (page.Count, page.Value.Length));
    }

    [Fact]
    public async Task PagesCoverEveryRowExactlyOnceEvenWhenTheSortValueRepeats()
    {
        // Twelve rows share the symbol: without the key as the last sort their order could differ between requests.
        var client = await SeededAdminAsync();

        var pages = new List<Page>();
        foreach (var skip in new[] { 0, 4, 8, 12 })
            pages.Add(await GetPageAsync(client, $"$orderby=symbol&$top=4&$skip={skip}"));
        var all = await GetPageAsync(client, "$orderby=symbol&$top=100");

        Assert.Equal([4, 4, 4, 1], pages.Select(p => p.Value.Length));
        Assert.Equal(all.Value.Select(r => r.Id), pages.SelectMany(p => p.Value).Select(r => r.Id));
        Assert.Equal(13, all.Value.Select(r => r.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("symbol eq 'ETH/USDT'", 1)]
    [InlineData("contains(tolower(symbol),'btc')", 12)]
    [InlineData("market eq 'Spot'", 1)]
    [InlineData("contains(tolower(exchange),'binance') or contains(tolower(symbolDescription),'eth')", 13)] // what the panel's search sends
    [InlineData("contains(tolower(symbol),'it''s')", 0)]                                                   // a quote is written twice
    [InlineData("intervalSeconds ge 3600 and market eq 'Futures'", 6)]
    [InlineData("collectOpenInterest eq true", 0)]
    [InlineData("startDate ge 2020-01-01T00:00:00Z", 13)]
    [InlineData("startDate gt 2020-01-01T00:00:00Z", 0)]
    [InlineData("startDate gt 2019-12-31T23:59:59Z", 13)]   // one second either side of the stored value: dates are read as UTC,
    [InlineData("startDate lt 2020-01-01T00:00:01Z", 13)]   // not in the time zone of the server
    [InlineData("startDate lt 2020-01-01T00:00:00Z", 0)]
    public async Task FilterNarrowsTheListAndTheCount(string filter, int expected)
    {
        var client = await SeededAdminAsync();

        var page = await GetPageAsync(client, $"$filter={Uri.EscapeDataString(filter)}");

        Assert.Equal((expected, expected), (page.Count, page.Value.Length));
    }

    [Fact]
    public async Task APageBeyondTheEndIsEmptyButStillReportsTheCount()
    {
        var client = await SeededAdminAsync();

        var page = await GetPageAsync(client, "$top=4&$skip=100");

        Assert.Equal((13, 0), (page.Count, page.Value.Length));
    }

    [Fact]
    public async Task TheRowsOfOneSymbolCanBeSortedShortestTimeFrameFirst()
    {
        // What the panel asks for; the order they were added in (and their ids) must not matter.
        var client = await SeededAdminAsync();

        var page = await GetPageAsync(client, "$filter=symbol eq 'BTC/USDT'&$orderby=symbol,intervalSeconds,id&$top=100");

        Assert.Equal(BtcTimeFrames.Order(), page.Value.Select(r => r.IntervalSeconds));
    }

    [Fact]
    public async Task OrderByDescendingPutsTheLargestFirst()
    {
        var client = await SeededAdminAsync();

        var page = await GetPageAsync(client, "$orderby=intervalSeconds desc&$top=2");

        Assert.Equal([43200, 28800], page.Value.Select(r => r.IntervalSeconds));
    }

    [Theory]
    [InlineData("$top=101")]                      // above the largest page
    [InlineData("$top=-1")]
    [InlineData("$select=symbol")]                // not one of the allowed options
    [InlineData("$expand=exchange")]
    [InlineData("$apply=groupby((symbol))")]
    [InlineData("$filter=nothing eq 1")]          // unknown property
    [InlineData("$filter=symbol eq")]             // not valid OData
    [InlineData("$orderby=nothing")]
    public async Task AQueryThatIsNotAllowedOrNotValidIsABadRequest(string query)
    {
        var client = await SeededAdminAsync();

        var response = await client.GetAsync($"{Url}?{query}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
