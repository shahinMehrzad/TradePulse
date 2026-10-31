using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>
/// A symbol that was never in the old enum: the admin adds it, lists it, collects it, and the dashboard and the candle routes
/// know it by the table-name part of the exchange's name for it. Own fixture: the dashboard tests count the cards of their database.
/// </summary>
public class NewSymbolRoutesEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Created(int Id);
    private record Card(string Exchange, string Market, string Symbol, string[] Timeframes);
    private record Cards(Card[] Items);
    private record Detail(string Exchange, string Market, string Symbol, string[] Timeframes);

    private async Task<HttpClient> AdminAsync()
    {
        var client = api.CreateClient();
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new { Email = ApiFixture.AdminEmail, Username = "", Password = ApiFixture.AdminPassword }, Ct);
        login.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken);
        return client;
    }

    private static async Task<int> CreatedIdAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
    }

    [Fact]
    public async Task ASymbolTheEnumNeverHadIsACardAndAnAddressableSeries()
    {
        var client = await AdminAsync();
        var symbol = await CreatedIdAsync(await client.PostAsJsonAsync("/api/admin/symbols", new { name = "1000PEPE/USDT" }, Ct));
        var listing = await CreatedIdAsync(await client.PostAsJsonAsync("/api/admin/symbols/listings",
            new { exchangeId = 1, marketId = 2, symbolId = symbol, exchangeSymbol = "1000pepeusdt" }, Ct));   // stored as Binance spells it: 1000PEPEUSDT
        await CreatedIdAsync(await client.PostAsJsonAsync("/api/admin/configurations/add-config",
            new { symbolListingId = listing, timeFrameId = 3600, startDate = "2020-01-01T00:00:00Z" }, Ct));

        var cards = (await client.GetFromJsonAsync<Cards>("/api/admin/dashboard/series?market=futures", Ct))!;
        var detail = await client.GetAsync("/api/admin/dashboard/series/binance/futures/1000PEPEUSDT", Ct);   // any case of the name

        var card = Assert.Single(cards.Items, c => c.Symbol == "1000pepeusdt");
        Assert.Equal(["1h"], card.Timeframes);
        Assert.Equal(("binance", "futures"), (card.Exchange, card.Market));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        Assert.Equal("1000pepeusdt", (await detail.Content.ReadFromJsonAsync<Detail>(Ct))!.Symbol);
    }

    [Fact]
    public async Task TheCandleRoutesAcceptAnyValidNameAndAnswerNotFoundWhenThereIsNoTableYet()
    {
        var client = await AdminAsync();

        var unknown = await client.GetAsync("/api/candles/binance/futures/1000pepeusdt/1h/latest?limit=5", Ct);
        var unsafeName = await client.GetAsync("/api/candles/binance/futures/btc%20usdt/1h/latest?limit=5", Ct);
        var known = await client.GetAsync(ApiFixture.CandleUrl, Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unsafeName.StatusCode);
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
    }
}
