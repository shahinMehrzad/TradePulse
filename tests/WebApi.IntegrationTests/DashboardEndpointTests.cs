using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Domain.Entities;
using Infrastructure.Repositories.TimeSeries;

namespace WebApi.IntegrationTests;

public class DashboardEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string SeriesUrl = "/api/admin/dashboard/series";
    private const string AddUrl = "/api/admin/configurations/add-config";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Snapshot(string Timeframe, string Status, int? LagSeconds, decimal? LastClose, decimal[]? Sparkline);
    private record Item(string Exchange, string Market, string Symbol, string[] Timeframes, string DefaultTimeframe, Snapshot Current);
    private record Response(DateTime GeneratedAt, Item[] Items);

    private async Task<HttpClient> AdminAsync()
    {
        var client = api.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { Email = ApiFixture.AdminEmail, Username = "", Password = ApiFixture.AdminPassword }, Ct);
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken);
        return client;
    }

    // Binance (1) / Futures (2) / symbol / time frame in seconds. BTCUSDT 1h is the series the fixture stores ten candles for.
    // The database is shared by the tests of this class, so a series another test already added is fine.
    private static async Task EnsureSeriesAsync(HttpClient client, int symbolId, int timeFrameId)
    {
        var response = await client.PostAsJsonAsync(
            AddUrl, new { exchangeId = 1, marketId = 2, symbolId, timeFrameId, startDate = "2020-01-01T00:00:00Z" }, Ct);

        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"adding the series returned {(int)response.StatusCode}");
    }

    [Fact]
    public async Task WithoutATokenItIsUnauthorized()
    {
        var response = await api.CreateClient().GetAsync(SeriesUrl, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AUnknownMarketIsABadRequest()
    {
        var client = await AdminAsync();

        var response = await client.GetAsync(SeriesUrl + "?market=options", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task EachConfiguredSymbolIsACardWithItsStoredCandles()
    {
        var client = await AdminAsync();
        await EnsureSeriesAsync(client, symbolId: 1, timeFrameId: 3600);   // BTCUSDT 1h: has rows
        await EnsureSeriesAsync(client, symbolId: 1, timeFrameId: 14400);  // BTCUSDT 4h: no table yet
        await EnsureSeriesAsync(client, symbolId: 3, timeFrameId: 3600);   // ETHUSDT 1h: no table yet

        var response = await client.GetFromJsonAsync<Response>(SeriesUrl + "?market=futures", Ct);

        Assert.Equal(2, response!.Items.Length);
        var btc = response.Items.Single(i => i.Symbol == "btcusdt");
        Assert.Equal(("binance", "futures"), (btc.Exchange, btc.Market));
        Assert.Equal(["1h", "4h"], btc.Timeframes);
        Assert.Equal("1h", btc.DefaultTimeframe);
        Assert.Equal(3m, btc.Current.LastClose);
        Assert.Equal(10, btc.Current.Sparkline!.Length);
        Assert.Equal("late", btc.Current.Status); // the fixture's candles are from 2025

        var eth = response.Items.Single(i => i.Symbol == "ethusdt");
        Assert.Equal("empty", eth.Current.Status);
        Assert.Null(eth.Current.LastClose);
    }

    [Fact]
    public async Task TheMarketFilterHidesTheOtherMarket()
    {
        var client = await AdminAsync();
        await EnsureSeriesAsync(client, symbolId: 1, timeFrameId: 3600);

        var spot = await client.GetFromJsonAsync<Response>(SeriesUrl + "?market=spot", Ct);

        Assert.Empty(spot!.Items);
    }

    private const string DetailUrl = SeriesUrl + "/binance/futures/btcusdt";

    private record Related(string DataType, string Status, int? LagSeconds, DateTime? LastTime);
    private record Detail(string Exchange, string Market, string Symbol, string[] Timeframes, string DefaultTimeframe, Snapshot Current, Related[] Related);

    private static async Task EnsureSymbolDataAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/admin/configurations/add-symbol-data", new
        {
            exchangeId = 1, marketId = 2, symbolId = 1, collectFundingRate = true, fundingStartDate = "2020-01-01T00:00:00Z", collectLiquidation = true
        }, Ct);

        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict, $"adding the symbol data returned {(int)response.StatusCode}");
    }

    [Fact]
    public async Task TheDetailWithoutATokenIsUnauthorized()
    {
        var response = await api.CreateClient().GetAsync(DetailUrl, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/dashboard/series/kraken/futures/btcusdt")]
    [InlineData("/api/admin/dashboard/series/binance/options/btcusdt")]
    [InlineData("/api/admin/dashboard/series/binance/futures/doge")]
    [InlineData("/api/admin/dashboard/series/binance/2/btcusdt")]               // numbers are not names
    [InlineData("/api/admin/dashboard/series/binance/futures/btcusdt?timeframe=7h")]
    [InlineData("/api/admin/dashboard/series/binance/futures/btcusdt?timeframe=3600")]
    public async Task UnknownNamesAreABadRequest(string url)
    {
        var client = await AdminAsync();

        var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/admin/dashboard/series/binance/spot/btcusdt")]                  // known names, but not collected
    [InlineData("/api/admin/dashboard/series/binance/futures/btcusdt?timeframe=1d")]  // BTCUSDT is collected, not at 1d
    public async Task ASeriesOrTimeFrameThatIsNotCollectedIsNotFound(string url)
    {
        var client = await AdminAsync();
        await EnsureSeriesAsync(client, symbolId: 1, timeFrameId: 3600);

        var response = await client.GetAsync(url, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task TheDetailSummarisesTheChosenTimeFrameAndListsWhatIsCollectedAroundIt()
    {
        var client = await AdminAsync();
        await EnsureSeriesAsync(client, symbolId: 1, timeFrameId: 3600);   // has rows
        await EnsureSeriesAsync(client, symbolId: 1, timeFrameId: 14400);  // no table yet
        await EnsureSymbolDataAsync(client);                               // no funding or liquidation table yet

        var byDefault = await client.GetFromJsonAsync<Detail>(DetailUrl, Ct);
        var fourHours = await client.GetFromJsonAsync<Detail>(DetailUrl + "?timeframe=4h", Ct);

        Assert.Equal(("binance", "futures", "btcusdt"), (byDefault!.Exchange, byDefault.Market, byDefault.Symbol));
        Assert.Equal(["1h", "4h"], byDefault.Timeframes);
        Assert.Equal("1h", byDefault.Current.Timeframe);
        Assert.Equal(3m, byDefault.Current.LastClose);
        Assert.Equal("late", byDefault.Current.Status); // the fixture's candles are from 2025

        Assert.Equal("4h", fourHours!.Current.Timeframe);
        Assert.Equal("empty", fourHours.Current.Status);
        Assert.Equal("1h", fourHours.DefaultTimeframe);

        Assert.Equal(["fundingrate", "liquidation"], byDefault.Related.Select(r => r.DataType));
        Assert.All(byDefault.Related, r => Assert.Equal("empty", r.Status));
    }

    [Fact]
    public async Task ACollectedFundingRateShowsItsNewestRow()
    {
        var client = await AdminAsync();
        await EnsureSeriesAsync(client, symbolId: 3, timeFrameId: 3600); // ETHUSDT 1h
        var response = await client.PostAsJsonAsync("/api/admin/configurations/add-symbol-data", new
        {
            exchangeId = 1, marketId = 2, symbolId = 3, collectFundingRate = true, fundingStartDate = "2020-01-01T00:00:00Z", collectLiquidation = false
        }, Ct);
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict);

        var funding = new TimeSeriesRepository<FundingRate>(api.ConnectionString, new FundingRateTable());
        const string table = "binance_futures_ethusdt_fundingrate";
        var newest = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        await funding.EnsureTableExistsAsync(table, Ct);
        await funding.BulkInsert(table, [new FundingRate(newest.AddHours(-8), 0.0001m, 3000m), new FundingRate(newest, 0.0002m, 3010m)], Ct);

        var detail = await client.GetFromJsonAsync<Detail>("/api/admin/dashboard/series/binance/futures/ethusdt", Ct);

        var related = Assert.Single(detail!.Related);
        Assert.Equal("fundingrate", related.DataType);
        Assert.Equal("late", related.Status); // the row is from 2025
        Assert.Equal(newest, related.LastTime);
        Assert.True(related.LagSeconds > 8 * 3600);
    }
}
