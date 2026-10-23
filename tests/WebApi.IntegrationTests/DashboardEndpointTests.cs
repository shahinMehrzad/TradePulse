using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

public class DashboardEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string SeriesUrl = "/api/dashboard/series";
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
}
