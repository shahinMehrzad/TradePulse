using System.Net;
using System.Net.Http.Json;

namespace Trader.IntegrationTests;

/// <summary>
/// What a server does before anybody has chosen a symbol. Its own class, so it has its own database: in the other class the tests
/// enable symbols, and the choice is the same for everybody.
/// </summary>
public class TradingSymbolDefaultTests(TraderFixture trader) : IClassFixture<TraderFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record SymbolView(string Symbol, bool Enabled);
    private record KeyView(Guid Id, string Status, string PublicKeyPem);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Ct))!;

    private Task<HttpResponseMessage> SymbolsAsync(TraderFixture.Caller caller) =>
        trader.SendAsync(HttpMethod.Get, "/trading/symbols", caller, elevated: false);

    private async Task<Guid> ActiveKeyAsync(TraderFixture.Caller caller)
    {
        var key = await ReadAsync<KeyView>(await trader.SendAsync(HttpMethod.Post, "/keys", caller, new { Label = "k", Environment = "Demo" }));
        var apiKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        trader.Binance.Register(apiKey, key.PublicKeyPem);
        trader.Binance.Permissions[apiKey] = FakeBinance.GoodPermissions;
        trader.Binance.MarkPrices["BTCUSDT"] = "50000";
        Assert.Equal(HttpStatusCode.OK, (await trader.SendAsync(HttpMethod.Put, $"/keys/{key.Id}/api-key", caller, new { ApiKey = apiKey })).StatusCode);
        return key.Id;
    }

    private Task<HttpResponseMessage> OrderAsync(TraderFixture.Caller caller, Guid keyId, string symbol, bool reduceOnly = false) =>
        trader.SendAsync(HttpMethod.Post, "/trading/orders", caller,
            new { KeyId = keyId, Symbol = symbol, Side = reduceOnly ? "SELL" : "BUY", Quantity = 0.001m, ReduceOnly = reduceOnly });

    [Fact]
    public async Task TheListHoldsEverySymbolTheServerAllowsAndNothingIsEnabledUntilSomebodyChooses()
    {
        var response = await SymbolsAsync(trader.Administrator());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var symbols = await ReadAsync<List<SymbolView>>(response);
        Assert.Equal(["BTCUSDT", "ETHUSDT"], symbols.Select(s => s.Symbol).Order());
        Assert.All(symbols, s => Assert.False(s.Enabled));
    }

    [Fact]
    public async Task ASymbolNobodyChoseCannotBeOpenedAndTheReasonIsSaid()
    {
        var admin = trader.Administrator("fresh-start");
        var key = await ActiveKeyAsync(admin);
        var before = trader.Binance.Orders.Count;

        var refused = await OrderAsync(admin, key, "BTCUSDT");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Contains("has not been enabled for trading in the panel", await refused.Content.ReadAsStringAsync(Ct));
        Assert.Equal(before, trader.Binance.Orders.Count);
    }

    [Fact]
    public async Task ClosingAPositionNeverNeedsTheSymbolToBeChosen()
    {
        var admin = trader.Administrator("closer");
        var key = await ActiveKeyAsync(admin);

        var closing = await OrderAsync(admin, key, "BTCUSDT", reduceOnly: true);

        Assert.Equal(HttpStatusCode.OK, closing.StatusCode);
    }
}
