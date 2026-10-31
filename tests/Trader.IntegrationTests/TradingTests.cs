using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace Trader.IntegrationTests;

/// <summary>
/// Orders from the outside: Demo against Live, the caps, the halt switch, and what is written down when Binance says no or says nothing.
/// Each test uses its own users, so budgets and halt switches do not leak between them.
/// </summary>
public class TradingTests(TraderFixture trader) : IClassFixture<TraderFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record KeyView(Guid Id, string Status, string PublicKeyPem, string Environment);
    private record OrderView(Guid Id, string Symbol, string Side, decimal Quantity, bool ReduceOnly, decimal NotionalUsd, string Status, long? ExchangeOrderId, string? Error);
    private record StateView(bool Halted, bool HaltedByServer, bool LiveAllowed, decimal MaxOrderUsd, decimal MaxDailyUsd, decimal OpenedTodayUsd);

    private static string NewApiKey() => Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Ct))!;

    /// <summary>A key Binance has accepted. Demo needs no permission answer; Live is given good permissions.</summary>
    private async Task<(KeyView Key, string ApiKey)> ActiveKeyAsync(TraderFixture.Caller caller, string environment)
    {
        var created = await trader.SendAsync(HttpMethod.Post, "/keys", caller, new { Label = "k", Environment = environment });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var key = await ReadAsync<KeyView>(created);
        var apiKey = NewApiKey();
        trader.Binance.Register(apiKey, key.PublicKeyPem);
        trader.Binance.Permissions[apiKey] = FakeBinance.GoodPermissions;
        trader.Binance.MarkPrices["BTCUSDT"] = "50000";
        trader.Binance.MarkPrices["ETHUSDT"] = "2500";

        var attached = await trader.SendAsync(HttpMethod.Put, $"/keys/{key.Id}/api-key", caller, new { ApiKey = apiKey });
        Assert.Equal("Active", (await ReadAsync<KeyView>(attached)).Status);
        return (key, apiKey);
    }

    private Task<HttpResponseMessage> OrderAsync(
        TraderFixture.Caller caller, Guid keyId, decimal quantity, string symbol = "BTCUSDT", string side = "BUY", bool reduceOnly = false,
        HttpClient? client = null, bool elevated = true) =>
        trader.SendAsync(HttpMethod.Post, "/trading/orders", caller, new { KeyId = keyId, Symbol = symbol, Side = side, Quantity = quantity, ReduceOnly = reduceOnly },
            elevated, client: client);

    private async Task<StateView> StateAsync(TraderFixture.Caller caller, HttpClient? client = null) =>
        await ReadAsync<StateView>(await trader.SendAsync(HttpMethod.Get, "/trading/state", caller, elevated: false, client: client));

    private static async Task<string> ProblemAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync(Ct);

    // ---- Demo and Live

    [Fact]
    public async Task ADemoKeyIsCheckedAndTradesOnTheDemoHostWithoutThePermissionCheck()
    {
        var admin = trader.Administrator();
        var (key, apiKey) = await ActiveKeyAsync(admin, "Demo");
        var before = trader.Binance.Orders.Count;

        var response = await OrderAsync(admin, key.Id, 0.001m);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var order = await ReadAsync<OrderView>(response);
        Assert.Equal("Placed", order.Status);
        Assert.NotNull(order.ExchangeOrderId);
        Assert.Equal(50m, order.NotionalUsd);
        Assert.Equal(before + 1, trader.Binance.Orders.Count);
        var sent = trader.Binance.Orders[^1];
        Assert.StartsWith("symbol=BTCUSDT&side=BUY&type=MARKET&quantity=0.001&newClientOrderId=", sent);
        Assert.Contains("demo-fapi.binance.com", trader.Binance.Hosts);
        Assert.DoesNotContain(trader.Binance.Requests, r => r.StartsWith("/sapi/") && r.EndsWith(apiKey[^4..]));
    }

    [Fact]
    public async Task ALiveKeyCannotTradeUntilTheServerOwnerAllowsIt()
    {
        var admin = trader.Administrator();
        var (key, _) = await ActiveKeyAsync(admin, "Live");
        var before = trader.Binance.Orders.Count;

        var refused = await OrderAsync(admin, key.Id, 0.001m);
        var allowed = await OrderAsync(admin, key.Id, 0.001m, client: trader.ClientWith(("Trading:AllowLive", "true")));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("Live trading is off", await ProblemAsync(refused));
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal(before + 1, trader.Binance.Orders.Count);
    }

    [Fact]
    public async Task AKeyThatBinanceHasNotAcceptedCannotTrade()
    {
        var admin = trader.Administrator();
        var created = await ReadAsync<KeyView>(await trader.SendAsync(HttpMethod.Post, "/keys", admin, new { Label = "k", Environment = "Demo" }));
        var before = trader.Binance.Orders.Count;

        var response = await OrderAsync(admin, created.Id, 0.001m);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, trader.Binance.Orders.Count);
    }

    [Fact]
    public async Task AnUnknownEnvironmentIsRefusedWhenMakingAKey()
    {
        var response = await trader.SendAsync(HttpMethod.Post, "/keys", trader.Administrator(), new { Label = "k", Environment = "Moon" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ---- who may

    [Fact]
    public async Task AnOrderNeedsAnAdministratorAndAFreshConfirmationAndAKeyOfTheirOwn()
    {
        var owner = trader.Administrator("owner");
        var intruder = trader.Administrator("intruder");
        var (key, _) = await ActiveKeyAsync(owner, "Demo");
        var before = trader.Binance.Orders.Count;

        var anonymous = await trader.SendAsync(HttpMethod.Post, "/trading/orders", null, new { KeyId = key.Id, Symbol = "BTCUSDT", Side = "BUY", Quantity = 0.001m });
        var ordinary = await OrderAsync(trader.OrdinaryUser(), key.Id, 0.001m);
        var unconfirmed = await OrderAsync(owner, key.Id, 0.001m, elevated: false);
        var notTheirs = await OrderAsync(intruder, key.Id, 0.001m);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, ordinary.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, unconfirmed.StatusCode);
        Assert.Contains("step_up_required", await ProblemAsync(unconfirmed));
        Assert.Equal(HttpStatusCode.NotFound, notTheirs.StatusCode);
        Assert.Equal(before, trader.Binance.Orders.Count);
    }

    [Fact]
    public async Task OrdersAreListedForTheirOwnerOnly()
    {
        var owner = trader.Administrator("lister");
        var (key, _) = await ActiveKeyAsync(owner, "Demo");
        await OrderAsync(owner, key.Id, 0.001m);

        var mine = await ReadAsync<OrderView[]>(await trader.SendAsync(HttpMethod.Get, "/trading/orders", owner, elevated: false));
        var theirs = await ReadAsync<OrderView[]>(await trader.SendAsync(HttpMethod.Get, "/trading/orders", trader.Administrator("other"), elevated: false));

        Assert.Single(mine);
        Assert.Empty(theirs);
    }

    // ---- limits

    [Theory]
    [InlineData("DOGEUSDT", "BUY", 1, HttpStatusCode.UnprocessableEntity)]
    [InlineData("BTCUSDT", "HOLD", 1, HttpStatusCode.BadRequest)]
    [InlineData("BTCUSDT", "BUY", 0, HttpStatusCode.BadRequest)]
    [InlineData("BTCUSDT", "BUY", -1, HttpStatusCode.BadRequest)]
    public async Task NonsenseIsRefusedBeforeBinanceIsAsked(string symbol, string side, int quantity, HttpStatusCode expected)
    {
        var admin = trader.Administrator();
        var (key, _) = await ActiveKeyAsync(admin, "Demo");
        var before = trader.Binance.Orders.Count;

        var response = await OrderAsync(admin, key.Id, quantity, symbol, side);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(before, trader.Binance.Orders.Count);
    }

    [Fact]
    public async Task AQuantityWithMoreThanEightDecimalsIsRefused()
    {
        var admin = trader.Administrator();
        var (key, _) = await ActiveKeyAsync(admin, "Demo");

        var response = await OrderAsync(admin, key.Id, 0.000000001m);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnOrderOverTheSizeCapIsRefusedAndNeverSent()
    {
        var admin = trader.Administrator();
        var (key, _) = await ActiveKeyAsync(admin, "Demo"); // 0.01 BTC at 50000 = 500 USDT, the cap is 100
        var before = trader.Binance.Orders.Count;

        var response = await OrderAsync(admin, key.Id, 0.01m);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Order too large", await ProblemAsync(response));
        Assert.Equal(before, trader.Binance.Orders.Count);
        Assert.Equal(0m, (await StateAsync(admin)).OpenedTodayUsd);
    }

    [Fact]
    public async Task TheDailyBudgetIsSpentByOpeningOrdersAndNeverBlocksClosingOnes()
    {
        var admin = trader.Administrator();
        var (key, _) = await ActiveKeyAsync(admin, "Demo");
        var client = trader.ClientWith(("Trading:MaxOrderUsd", "100"), ("Trading:MaxDailyUsd", "150")); // 0.002 BTC = 100 USDT

        var first = await OrderAsync(admin, key.Id, 0.002m, client: client);
        var second = await OrderAsync(admin, key.Id, 0.002m, client: client);
        var closing = await OrderAsync(admin, key.Id, 0.01m, side: "SELL", reduceOnly: true, client: client);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, second.StatusCode);
        Assert.Contains("Daily limit", await ProblemAsync(second));
        Assert.Equal(HttpStatusCode.OK, closing.StatusCode); // larger than the order cap, but it only reduces
        Assert.EndsWith("&reduceOnly=true", trader.Binance.Orders[^1].Split("&timestamp=")[0]);
        Assert.Equal(100m, (await StateAsync(admin, client)).OpenedTodayUsd);
    }

    [Fact]
    public async Task WithoutAPriceTheSizeCannotBeCheckedSoNothingIsSent()
    {
        var admin = trader.Administrator();
        var (key, _) = await ActiveKeyAsync(admin, "Demo");
        trader.Binance.MarkPrices.Remove("ETHUSDT");
        var before = trader.Binance.Orders.Count;

        var response = await OrderAsync(admin, key.Id, 0.01m, "ETHUSDT");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(before, trader.Binance.Orders.Count);
    }

    // ---- halt

    [Fact]
    public async Task HaltingStopsNewPositionsButNotClosingAndResumingNeedsConfirmation()
    {
        var admin = trader.Administrator();
        var (key, _) = await ActiveKeyAsync(admin, "Demo");

        var halt = await trader.SendAsync(HttpMethod.Post, "/trading/halt", admin, elevated: false); // no extra code in an emergency
        var opening = await OrderAsync(admin, key.Id, 0.001m);
        var closing = await OrderAsync(admin, key.Id, 0.001m, side: "SELL", reduceOnly: true);
        var resumeUnconfirmed = await trader.SendAsync(HttpMethod.Post, "/trading/resume", admin, elevated: false);
        var stateHalted = await StateAsync(admin);
        var resume = await trader.SendAsync(HttpMethod.Post, "/trading/resume", admin);
        var openingAgain = await OrderAsync(admin, key.Id, 0.001m);

        Assert.Equal(HttpStatusCode.NoContent, halt.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, opening.StatusCode);
        Assert.Contains("halted", await ProblemAsync(opening));
        Assert.Equal(HttpStatusCode.OK, closing.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, resumeUnconfirmed.StatusCode);
        Assert.True(stateHalted.Halted);
        Assert.Equal(HttpStatusCode.NoContent, resume.StatusCode);
        Assert.Equal(HttpStatusCode.OK, openingAgain.StatusCode);
    }

    [Fact]
    public async Task HaltingOneUserDoesNotStopAnother()
    {
        var one = trader.Administrator("halting");
        var two = trader.Administrator("working");
        var (keyTwo, _) = await ActiveKeyAsync(two, "Demo");

        await trader.SendAsync(HttpMethod.Post, "/trading/halt", one, elevated: false);

        Assert.True((await StateAsync(one)).Halted);
        Assert.Equal(HttpStatusCode.OK, (await OrderAsync(two, keyTwo.Id, 0.001m)).StatusCode);
    }

    [Fact]
    public async Task TheServerCanHaltEveryoneAndTheUserCannotResumeThat()
    {
        var admin = trader.Administrator();
        var (key, _) = await ActiveKeyAsync(admin, "Demo");
        var client = trader.ClientWith(("Trading:Halted", "true"));

        var opening = await OrderAsync(admin, key.Id, 0.001m, client: client);
        await trader.SendAsync(HttpMethod.Post, "/trading/resume", admin, client: client);
        var state = await StateAsync(admin, client);

        Assert.Equal(HttpStatusCode.Conflict, opening.StatusCode);
        Assert.True(state.Halted);
        Assert.True(state.HaltedByServer);
    }

    // ---- what Binance answers

    [Fact]
    public async Task AnOrderBinanceRefusedIsRecordedAsFailedAndSpendsNoBudget()
    {
        var admin = trader.Administrator();
        var (key, _) = await ActiveKeyAsync(admin, "Demo");
        trader.Binance.OrderAnswer = () => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"code":-2019,"msg":"Margin is insufficient."}""", Encoding.UTF8, "application/json")
        };
        try
        {
            var order = await ReadAsync<OrderView>(await OrderAsync(admin, key.Id, 0.001m));

            Assert.Equal("Failed", order.Status);
            Assert.Contains("-2019", order.Error);
            Assert.Equal(0m, (await StateAsync(admin)).OpenedTodayUsd);
        }
        finally
        {
            trader.Binance.OrderAnswer = null;
        }
    }

    [Fact]
    public async Task AnOrderWithoutAnAnswerIsRecordedAsUnknownAndKeepsCountingAgainstTheBudget()
    {
        var admin = trader.Administrator();
        var (key, _) = await ActiveKeyAsync(admin, "Demo");
        trader.Binance.OrderAnswer = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"code":-1007,"msg":"Timeout waiting for response from backend server. Send status unknown."}""", Encoding.UTF8, "application/json")
        };
        try
        {
            var order = await ReadAsync<OrderView>(await OrderAsync(admin, key.Id, 0.001m));

            Assert.Equal("Unknown", order.Status);
            Assert.Equal(50m, (await StateAsync(admin)).OpenedTodayUsd);
        }
        finally
        {
            trader.Binance.OrderAnswer = null;
        }
    }
}
