using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Domain.Entities;
using Infrastructure.Repositories.TimeSeries;

namespace WebApi.IntegrationTests;

/// <summary>The long/short ratio and taker volume rows the series page charts: what the endpoint answers, from the real tables.</summary>
public class MarketStatisticsEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Base = "/api/admin/dashboard/series/binance/futures";
    private const string AddUrl = "/api/admin/configurations/add-config";

    private static readonly DateTime Start = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record LongShort(DateTime Time, decimal Ratio, decimal LongAccount, decimal ShortAccount);
    private record Taker(DateTime Time, decimal BuySellRatio, decimal BuyVolume, decimal SellVolume);
    private record Stats(string Exchange, string Market, string Symbol, string Timeframe, bool Collected, LongShort[] LongShort, Taker[] TakerVolume);

    private async Task<HttpClient> AdminAsync()
    {
        var client = api.CreateClient();
        var login = await api.AdminLoginAsync(client, Ct);
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken);
        return client;
    }

    // The listing ids of the seeded Binance Futures symbols are the symbol ids (BTCUSDT 1, ETHUSDT 3, SOLUSDT 4).
    private static async Task EnsureSeriesAsync(HttpClient client, int listingId, int timeFrameId, bool statistics)
    {
        var response = await client.PostAsJsonAsync(
            AddUrl, new { symbolListingId = listingId, timeFrameId, startDate = "2020-01-01T00:00:00Z", collectMarketStatistics = statistics }, Ct);

        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"adding the series returned {(int)response.StatusCode}");
    }

    [Fact]
    public async Task WithoutATokenItIsUnauthorized()
    {
        var response = await api.CreateClient().GetAsync($"{Base}/btcusdt/statistics?timeframe=1h", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("btcusdt/statistics")]                       // the statistics are kept per time frame
    [InlineData("btcusdt/statistics?timeframe=2weeks")]
    [InlineData("btcusdt/statistics?timeframe=1h&points=1")]
    [InlineData("btcusdt/statistics?timeframe=1h&points=201")]
    [InlineData("btc;usdt/statistics?timeframe=1h")]
    public async Task ARequestTheApiCannotUnderstandIsABadRequest(string path)
    {
        var response = await (await AdminAsync()).GetAsync($"{Base}/{path}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ASeriesThatIsNotCollectedAtThatTimeFrameIsNotFound()
    {
        var client = await AdminAsync();
        await EnsureSeriesAsync(client, listingId: 1, timeFrameId: 3600, statistics: false);

        // 2h: no other test of this class collects it for BTCUSDT (they share one database).
        var response = await client.GetAsync($"{Base}/btcusdt/statistics?timeframe=2h", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ASeriesThatDoesNotCollectThemIsNotAnErrorJustNotCollected()
    {
        var client = await AdminAsync();
        await EnsureSeriesAsync(client, listingId: 4, timeFrameId: 3600, statistics: false);   // SOLUSDT 1h

        var stats = await client.GetFromJsonAsync<Stats>($"{Base}/solusdt/statistics?timeframe=1h", Ct);

        Assert.False(stats!.Collected);
        Assert.Empty(stats.LongShort);
        Assert.Empty(stats.TakerVolume);
    }

    [Fact]
    public async Task ASeriesThatCollectsThemBeforeTheTablesExistHasEmptyLists()
    {
        var client = await AdminAsync();
        await EnsureSeriesAsync(client, listingId: 3, timeFrameId: 3600, statistics: true);   // ETHUSDT 1h; the collector is not running here

        var stats = await client.GetFromJsonAsync<Stats>($"{Base}/ethusdt/statistics?timeframe=1h", Ct);

        Assert.True(stats!.Collected);
        Assert.Empty(stats.LongShort);
        Assert.Empty(stats.TakerVolume);
    }

    [Fact]
    public async Task TheNewestRowsComeBackOldestFirstWithTheirValues()
    {
        var client = await AdminAsync();
        await EnsureSeriesAsync(client, listingId: 1, timeFrameId: 14400, statistics: true);   // BTCUSDT 4h

        var longShort = new TimeSeriesRepository<LongShortRatio>(api.ConnectionString, new LongShortRatioTable());
        var taker = new TimeSeriesRepository<TakerVolume>(api.ConnectionString, new TakerVolumeTable());
        const string longShortTable = "binance_futures_btcusdt_4h_longshortratio";
        const string takerTable = "binance_futures_btcusdt_4h_takervolume";
        await longShort.EnsureTableExistsAsync(longShortTable, Ct);
        await taker.EnsureTableExistsAsync(takerTable, Ct);

        // Five periods, inserted in a scrambled order: what comes back is by time, not by insertion.
        int[] order = [3, 0, 4, 1, 2];
        await longShort.BulkInsert(longShortTable, order.Select(i => new LongShortRatio(Start.AddHours(4 * i), 1m + i / 10m, 0.5m + i / 100m, 0.5m - i / 100m)), Ct);
        await taker.BulkInsert(takerTable, order.Select(i => new TakerVolume(Start.AddHours(4 * i), 0.9m + i / 10m, 100m + i, 110m - i)), Ct);

        var stats = await client.GetFromJsonAsync<Stats>($"{Base}/btcusdt/statistics?timeframe=4h&points=3", Ct);

        Assert.True(stats!.Collected);
        Assert.Equal("4h", stats.Timeframe);
        // the newest three of five, oldest of those first
        Assert.Equal([Start.AddHours(8), Start.AddHours(12), Start.AddHours(16)], stats.LongShort.Select(r => r.Time));
        Assert.Equal((1.2m, 0.52m, 0.48m), (stats.LongShort[0].Ratio, stats.LongShort[0].LongAccount, stats.LongShort[0].ShortAccount));
        Assert.Equal([Start.AddHours(8), Start.AddHours(12), Start.AddHours(16)], stats.TakerVolume.Select(r => r.Time));
        // the second of the three is the fourth period (i = 3): 0.9 + 0.3, 100 + 3, 110 - 3
        Assert.Equal((1.2m, 103m, 107m), (stats.TakerVolume[1].BuySellRatio, stats.TakerVolume[1].BuyVolume, stats.TakerVolume[1].SellVolume));
    }
}
