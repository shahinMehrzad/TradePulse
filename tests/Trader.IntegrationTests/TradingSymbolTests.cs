using System.Net;
using System.Net.Http.Json;
using Dapper;
using Npgsql;

namespace Trader.IntegrationTests;

/// <summary>
/// Which of the server's symbols may be opened. The server's list is the ceiling (nothing here can go beyond it); an administrator
/// chooses among them in the panel, and a symbol nobody chose cannot be opened. Every test uses its own symbols and users where it can,
/// because the choice is the same for everybody.
/// </summary>
public class TradingSymbolTests(TraderFixture trader) : IClassFixture<TraderFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record SymbolView(string Symbol, bool Enabled);
    private record KeyView(Guid Id, string Status, string PublicKeyPem);
    private record StateView(IReadOnlyList<string> AllowedSymbols);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<T>(Ct))!;

    private Task<HttpResponseMessage> SymbolsAsync(TraderFixture.Caller caller) =>
        trader.SendAsync(HttpMethod.Get, "/trading/symbols", caller, elevated: false);

    private Task<HttpResponseMessage> EnableAsync(TraderFixture.Caller caller, string symbol, bool elevated = true) =>
        trader.SendAsync(HttpMethod.Post, $"/trading/symbols/{symbol}/enable", caller, elevated: elevated);

    private Task<HttpResponseMessage> DisableAsync(TraderFixture.Caller caller, string symbol) =>
        trader.SendAsync(HttpMethod.Post, $"/trading/symbols/{symbol}/disable", caller, elevated: false);

    private async Task<Guid> ActiveKeyAsync(TraderFixture.Caller caller)
    {
        var key = await ReadAsync<KeyView>(await trader.SendAsync(HttpMethod.Post, "/keys", caller, new { Label = "k", Environment = "Demo" }));
        var apiKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        trader.Binance.Register(apiKey, key.PublicKeyPem);
        trader.Binance.Permissions[apiKey] = FakeBinance.GoodPermissions;
        trader.Binance.MarkPrices["BTCUSDT"] = "50000";
        trader.Binance.MarkPrices["ETHUSDT"] = "2500";
        Assert.Equal(HttpStatusCode.OK, (await trader.SendAsync(HttpMethod.Put, $"/keys/{key.Id}/api-key", caller, new { ApiKey = apiKey })).StatusCode);
        return key.Id;
    }

    private Task<HttpResponseMessage> OrderAsync(TraderFixture.Caller caller, Guid keyId, string symbol, bool reduceOnly = false) =>
        trader.SendAsync(HttpMethod.Post, "/trading/orders", caller,
            new { KeyId = keyId, Symbol = symbol, Side = reduceOnly ? "SELL" : "BUY", Quantity = 0.001m, ReduceOnly = reduceOnly });

    [Fact]
    public async Task EnablingNeedsAFreshSecondFactorButDisablingIsOneClick()
    {
        var admin = trader.Administrator("chooser");

        var unconfirmed = await EnableAsync(admin, "BTCUSDT", elevated: false);
        var enabled = await EnableAsync(admin, "BTCUSDT");
        var disabled = await DisableAsync(admin, "BTCUSDT");

        Assert.Equal(HttpStatusCode.Forbidden, unconfirmed.StatusCode);
        Assert.Contains("step_up_required", await unconfirmed.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.NoContent, enabled.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, disabled.StatusCode);
    }

    [Fact]
    public async Task OnlyAdministratorsChooseAndOnlyAmongTheSymbolsTheServerAllows()
    {
        var ordinary = trader.OrdinaryUser();
        var admin = trader.Administrator();

        Assert.Equal(HttpStatusCode.Forbidden, (await SymbolsAsync(ordinary)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await EnableAsync(ordinary, "BTCUSDT")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DisableAsync(ordinary, "BTCUSDT")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await trader.SendAsync(HttpMethod.Get, "/trading/symbols", null)).StatusCode);

        // The server's list is the ceiling: a symbol that is not on it cannot be added from the panel, whatever the confirmation.
        var beyond = await EnableAsync(admin, "DOGEUSDT");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, beyond.StatusCode);
        Assert.Contains("Symbol not allowed", await beyond.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, (await DisableAsync(admin, "DOGEUSDT")).StatusCode);
    }

    [Fact]
    public async Task AnEnabledSymbolIsOpenedAndASymbolSwitchedOffCannotBeOpenedAgainButCanStillBeClosed()
    {
        var admin = trader.Administrator("switcher");
        var key = await ActiveKeyAsync(admin);
        await EnableAsync(admin, "BTCUSDT");

        var opened = await OrderAsync(admin, key, "BTCUSDT");
        await DisableAsync(admin, "BTCUSDT");
        var openAgain = await OrderAsync(admin, key, "BTCUSDT");
        var closing = await OrderAsync(admin, key, "BTCUSDT", reduceOnly: true);

        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, openAgain.StatusCode);
        Assert.Equal(HttpStatusCode.OK, closing.StatusCode);      // a position can never be locked in by switching its symbol off
        await EnableAsync(admin, "BTCUSDT");
    }

    [Fact]
    public async Task TheStateShowsOnlyTheSymbolsThatCanBeOpenedNow()
    {
        var admin = trader.Administrator("state");
        await EnableAsync(admin, "BTCUSDT");

        var state = await ReadAsync<StateView>(await trader.SendAsync(HttpMethod.Get, "/trading/state", admin, elevated: false));

        Assert.Contains("BTCUSDT", state.AllowedSymbols);
        await DisableAsync(admin, "BTCUSDT");
        state = await ReadAsync<StateView>(await trader.SendAsync(HttpMethod.Get, "/trading/state", admin, elevated: false));
        Assert.DoesNotContain("BTCUSDT", state.AllowedSymbols);
        await EnableAsync(admin, "BTCUSDT");
    }

    [Fact]
    public async Task EveryChoiceIsWrittenToTheAuditLogWithWhoMadeIt()
    {
        var admin = trader.Administrator("audited-chooser");

        await EnableAsync(admin, "ETHUSDT");
        await DisableAsync(admin, "ETHUSDT");
        await EnableAsync(admin, "ETHUSDT");

        await using var connection = new NpgsqlConnection(trader.ConnectionString);
        var rows = (await connection.QueryAsync<(string Action, string Summary, string Details)>(
            """SELECT "Action", "Summary", "Details" FROM config.audit_log WHERE "UserName" = 'audited-chooser' ORDER BY "Id" """)).ToList();
        Assert.Equal(["ETHUSDT enabled for trading", "ETHUSDT disabled for trading", "ETHUSDT enabled for trading"], rows.Select(r => r.Details));
        Assert.All(rows, r => Assert.Equal(("Updated", "trading"), (r.Action, r.Summary)));
    }

    [Fact]
    public async Task TheChoiceIsKeptForTheWatchToReadAndItIsTheSameForEveryone()
    {
        var one = trader.Administrator("first");
        var two = trader.Administrator("second");
        await EnableAsync(one, "BTCUSDT");

        var seenByTheOther = await ReadAsync<List<SymbolView>>(await SymbolsAsync(two));

        Assert.True(seenByTheOther.Single(s => s.Symbol == "BTCUSDT").Enabled);
        await using var connection = new NpgsqlConnection(trader.ConnectionString);
        Assert.Contains("BTCUSDT", await connection.QueryAsync<string>("SELECT symbol FROM vault.trading_symbols WHERE enabled"));
    }
}
