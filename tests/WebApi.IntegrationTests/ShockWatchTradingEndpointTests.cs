using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;

namespace WebApi.IntegrationTests;

/// <summary>
/// The symbols enabled for trading are watched on their own, and the admin can switch that off. The trading service publishes the
/// enabled symbols as a copy in <c>config.traded_symbols</c> (a table of the API's own migrations); here it is filled by hand, as the service would.
/// </summary>
public class ShockWatchTradingEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string WatchUrl = "/api/admin/shock-watch";
    private const string AddSeriesUrl = "/api/admin/configurations/add-config";
    private static string OffUrl(int listingId) => $"{WatchUrl}/traded/{listingId}/off";
    private static string EnabledUrl(int id) => $"{WatchUrl}/{id}/enabled";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Created(int Id);
    private record Problem(int Status, string? Title, string? Detail);
    private record Row(int Id, int SymbolListingId, string ExchangeSymbol, string Reason, bool IsEnabled, bool IsTradedNow, bool HasMinuteCandles);

    // The migration lists these on Binance Futures with the ids the symbol enum had.
    private static readonly string[] Names = ["", "BTCUSDT", "XAUUSDT", "ETHUSDT", "SOLUSDT", "XRPUSDT", "BNBUSDT", "SUIUSDT", "ADAUSDT", "DOGEUSDT", "LINKUSDT", "LTCUSDT", "AVAXUSDT", "DOTUSDT", "TRXUSDT"];

    // The database is shared by the tests of this class, and the trading choice and the watch list are global, so every test takes a symbol of its own.
    private static int _nextSymbol;
    private static int NewSymbol()
    {
        var symbol = Interlocked.Increment(ref _nextSymbol);
        Assert.True(symbol <= 14, "this class has run out of symbols; give the lookup more or share one");
        return symbol;
    }

    private async Task<HttpClient> AdminAsync()
    {
        var client = api.CreateClient();
        var response = await api.AdminLoginAsync(client, Ct);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>What the trading service does to the copy when an admin enables (or later disables) a symbol.</summary>
    private async Task SetTradedAsync(int symbolId, bool enabled)
    {
        await using var connection = new NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(enabled
            ? "INSERT INTO config.traded_symbols VALUES (@symbol, now() at time zone 'utc') ON CONFLICT (symbol) DO NOTHING"
            : "DELETE FROM config.traded_symbols WHERE symbol = @symbol", connection);
        command.Parameters.AddWithValue("symbol", Names[symbolId]);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<Row?> FindAsync(HttpClient client, int symbolId) =>
        (await client.GetFromJsonAsync<Row[]>(WatchUrl, Ct))!.SingleOrDefault(r => r.SymbolListingId == symbolId);

    [Fact]
    public async Task WithoutATokenTheSwitchOffIsUnauthorized()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.CreateClient().PostAsync(OffUrl(1), null, Ct)).StatusCode);
    }

    [Fact]
    public async Task ASymbolEnabledForTradingShowsOnTheListAsWatchedOnItsOwnWithNoRow()
    {
        var client = await AdminAsync();
        var traded = NewSymbol();
        var notTraded = NewSymbol();
        await SetTradedAsync(traded, true);

        var row = await FindAsync(client, traded);

        Assert.NotNull(row);
        Assert.Equal((0, Names[traded], "Trading", true, true, false), (row.Id, row.ExchangeSymbol, row.Reason, row.IsEnabled, row.IsTradedNow, row.HasMinuteCandles));
        Assert.Null(await FindAsync(client, notTraded));
    }

    [Fact]
    public async Task SwitchingOffATradedSymbolLeavesARowThatCanBeSwitchedBackOnToReturnToTheAutomaticWatch()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        await SetTradedAsync(symbol, true);

        var off = await client.PostAsync(OffUrl(symbol), null, Ct);

        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        var id = (await off.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
        var row = await FindAsync(client, symbol);
        Assert.Equal((id, "Trading", false, true), (row!.Id, row.Reason, row.IsEnabled, row.IsTradedNow));

        // Switching it on needs no 1 minute candles (none are collected here): a traded symbol cannot be refused its watch.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(EnabledUrl(id), new { enabled = true }, Ct)).StatusCode);
        var back = await FindAsync(client, symbol);
        Assert.Equal((0, true), (back!.Id, back.IsEnabled));
    }

    [Fact]
    public async Task OnlyASymbolEnabledForTradingHasAnAutomaticWatchToSwitchOff()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();

        var response = await client.PostAsync(OffUrl(symbol), null, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("not enabled for trading", (await response.Content.ReadFromJsonAsync<Problem>(Ct))!.Detail);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(OffUrl(999_999), null, Ct)).StatusCode);
    }

    [Fact]
    public async Task TheRowOfATradedSymbolIsNotRemovedBecauseSwitchingItOnIsTheWay()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        await SetTradedAsync(symbol, true);
        var id = (await (await client.PostAsync(OffUrl(symbol), null, Ct)).Content.ReadFromJsonAsync<Created>(Ct))!.Id;

        var response = await client.DeleteAsync($"{WatchUrl}/{id}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Switch its watch on", (await response.Content.ReadFromJsonAsync<Problem>(Ct))!.Detail);
        Assert.NotNull(await FindAsync(client, symbol));
    }

    [Fact]
    public async Task ASymbolThatStopsBeingTradedLeavesTheListButTheAdminsOwnRowStaysAndSaysItIsNoLongerTraded()
    {
        var client = await AdminAsync();
        var automatic = NewSymbol();
        var switchedOff = NewSymbol();
        await SetTradedAsync(automatic, true);
        await SetTradedAsync(switchedOff, true);
        await client.PostAsync(OffUrl(switchedOff), null, Ct);

        await SetTradedAsync(automatic, false);
        await SetTradedAsync(switchedOff, false);

        Assert.Null(await FindAsync(client, automatic));
        Assert.False((await FindAsync(client, switchedOff))!.IsTradedNow);
    }

    [Fact]
    public async Task ASymbolAddedByHandAndAlsoTradedIsOneRowTheAdminCanSwitchOff()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        (await client.PostAsJsonAsync(AddSeriesUrl,
            new { symbolListingId = ListingIds.Of(1, 2, symbol), timeFrameId = 60, startDate = "2020-01-01T00:00:00" }, Ct)).EnsureSuccessStatusCode();
        var id = (await (await client.PostAsJsonAsync(WatchUrl, new { symbolListingId = ListingIds.Of(1, 2, symbol) }, Ct)).Content.ReadFromJsonAsync<Created>(Ct))!.Id;
        await SetTradedAsync(symbol, true);

        var row = await FindAsync(client, symbol);
        Assert.Equal((id, "Manual", true, true, true), (row!.Id, row.Reason, row.IsEnabled, row.IsTradedNow, row.HasMinuteCandles));

        var off = await client.PostAsync(OffUrl(symbol), null, Ct);
        Assert.Equal(id, (await off.Content.ReadFromJsonAsync<Created>(Ct))!.Id);        // the same row: only its switch changed
        var after = await FindAsync(client, symbol);
        Assert.Equal(("Manual", false), (after!.Reason, after.IsEnabled));
    }
}
