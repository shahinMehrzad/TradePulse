using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>
/// The paged, searchable list the admin panel shows. Thirteen series are stored once for the whole class: twelve BTCUSDT
/// futures series (one per time frame, so many rows share a symbol name) and one ETHUSDT spot series.
/// </summary>
public class CandleWorkerConfigurationPagingTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string ListUrl = "/api/admin/configurations/candle-worker";
    private static readonly int[] BtcTimeFrames = [1, 60, 180, 300, 900, 1800, 3600, 7200, 14400, 21600, 28800, 43200];

    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static bool _seeded;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Row(int Id, int SymbolId, int TimeFrameId);
    private record Page(int TotalItemsCount, int TotalPages, int ItemsPerPage, int CurrentPage, Row[] Data);
    private record Problem(int Status, string? Detail);

    private async Task<HttpClient> SeededAdminAsync()
    {
        var client = api.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { Email = ApiFixture.AdminEmail, Username = "", Password = ApiFixture.AdminPassword }, Ct);
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
            new { exchangeId = 1, marketId, symbolId, timeFrameId, startDate = "2020-01-01T00:00:00Z" }, Ct)).EnsureSuccessStatusCode();

    private static async Task<Page> GetPageAsync(HttpClient client, string query) =>
        (await client.GetFromJsonAsync<Page>($"{ListUrl}?{query}", Ct))!;

    [Fact]
    public async Task WithoutATokenTheListIsUnauthorized()
    {
        var response = await api.CreateClient().GetAsync(ListUrl, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithoutParametersTheFirstTenAreReturned()
    {
        var client = await SeededAdminAsync();

        var page = await GetPageAsync(client, "");

        Assert.Equal((13, 2, 10, 1, 10), (page.TotalItemsCount, page.TotalPages, page.ItemsPerPage, page.CurrentPage, page.Data.Length));
    }

    [Fact]
    public async Task PagesCoverEveryRowExactlyOnceInAStableOrder()
    {
        // Twelve rows share the symbol BTCUSDT: without a tie-breaker their order could change between requests.
        var client = await SeededAdminAsync();

        var pages = new List<Page>();
        for (var number = 1; number <= 4; number++)
            pages.Add(await GetPageAsync(client, $"PageNumber={number}&PageSize=4"));
        var all = (await GetPageAsync(client, "PageNumber=1&PageSize=100")).Data.Select(r => r.Id).ToList();

        Assert.All(pages, p => Assert.Equal((13, 4), (p.TotalItemsCount, p.TotalPages)));
        Assert.Equal([4, 4, 4, 1], pages.Select(p => p.Data.Length));
        Assert.Equal(all, pages.SelectMany(p => p.Data).Select(r => r.Id));           // same order as the full list, no repeats, no gaps
        Assert.Equal(13, all.Distinct().Count());
        Assert.Equal(all.OrderBy(id => id).Take(12), all.Take(12).OrderBy(id => id)); // BTCUSDT rows are ordered by id
    }

    [Fact]
    public async Task ThePagingNumbersDescribeTheFilteredListNotTheWholeTable()
    {
        var client = await SeededAdminAsync();

        var eth = await GetPageAsync(client, "PageNumber=1&PageSize=5&Keyword=eth");
        var btc = await GetPageAsync(client, "PageNumber=1&PageSize=5&Keyword=BTC");
        var none = await GetPageAsync(client, "PageNumber=1&PageSize=5&Keyword=nothing-like-this");

        Assert.Equal((1, 1, 1), (eth.TotalItemsCount, eth.TotalPages, eth.Data.Length));
        Assert.Equal((12, 3, 5), (btc.TotalItemsCount, btc.TotalPages, btc.Data.Length));
        Assert.Equal((0, 0, 0), (none.TotalItemsCount, none.TotalPages, none.Data.Length));
    }

    [Theory]
    [InlineData("futures", 12)]
    [InlineData("SPOT", 1)]
    [InlineData("binance", 13)]
    [InlineData("  Btc  ", 12)]
    public async Task TheKeywordIgnoresCaseAndSurroundingSpacesAndSearchesEveryName(string keyword, int expected)
    {
        var client = await SeededAdminAsync();

        var page = await GetPageAsync(client, $"PageNumber=1&PageSize=100&Keyword={Uri.EscapeDataString(keyword)}");

        Assert.Equal(expected, page.TotalItemsCount);
    }

    [Fact]
    public async Task TheBiggestAllowedPageIsServed()
    {
        var client = await SeededAdminAsync();

        var page = await GetPageAsync(client, "PageNumber=1&PageSize=100");

        Assert.Equal((13, 1, 13), (page.TotalItemsCount, page.TotalPages, page.Data.Length));
    }

    [Fact]
    public async Task APageBeyondTheEndIsEmptyButStillReportsTheTotals()
    {
        var client = await SeededAdminAsync();

        var page = await GetPageAsync(client, "PageNumber=9&PageSize=4");

        Assert.Equal((13, 4, 0), (page.TotalItemsCount, page.TotalPages, page.Data.Length));
    }

    [Theory]
    [InlineData("PageNumber=0&PageSize=5")]
    [InlineData("PageNumber=-1&PageSize=5")]
    [InlineData("PageNumber=1&PageSize=0")]
    [InlineData("PageNumber=1&PageSize=-3")]
    [InlineData("PageNumber=1&PageSize=101")]                 // one more than the biggest page
    [InlineData("PageNumber=4&PageSize=1000000000")]          // would overflow the row offset if it were allowed
    [InlineData("PageNumber=3&PageSize=2147483647")]
    public async Task ANonsensePageIsBadRequestWithAReasonNotAServerError(string query)
    {
        var client = await SeededAdminAsync();

        var response = await client.GetAsync($"{ListUrl}?{query}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull((await response.Content.ReadFromJsonAsync<Problem>(Ct))!.Detail);
    }
}
