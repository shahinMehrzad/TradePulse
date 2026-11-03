using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>The watch list: the symbols the live watch follows, apart from what the collector collects.</summary>
public class ShockWatchEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string WatchUrl = "/api/admin/shock-watch";
    private const string AddSeriesUrl = "/api/admin/configurations/add-config";
    private static string DeleteSeriesUrl(int id) => $"/api/admin/configurations/delete-config/{id}";
    private static string EnabledUrl(int id) => $"{WatchUrl}/{id}/enabled";

    private const int OneMinute = 60;
    private const int OneHour = 3600;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Created(int Id);
    private record Problem(int Status, string? Title, string? Detail);
    private record Row(int Id, int SymbolListingId, int ExchangeId, int MarketId, int SymbolId, string ExchangeSymbol, string Reason, bool IsEnabled, bool HasMinuteCandles);

    // The database is shared by the tests of this class and a symbol can be on the list once, so every test that saves takes a symbol
    // of its own (the lookup holds 14). Tests that are refused before anything is saved use symbol 1 or an unknown listing.
    private static int _nextSymbol = 1;
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

    private static async Task<int> CollectAsync(HttpClient client, int symbolId, int timeFrameId = OneMinute)
    {
        var response = await client.PostAsJsonAsync(AddSeriesUrl,
            new { symbolListingId = ListingIds.Of(1, 2, symbolId), timeFrameId, startDate = "2020-01-01T00:00:00" }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
    }

    private static Task<HttpResponseMessage> WatchAsync(HttpClient client, int symbolId) =>
        client.PostAsJsonAsync(WatchUrl, new { symbolListingId = ListingIds.Of(1, 2, symbolId) }, Ct);

    private static async Task<int> AddAsync(HttpClient client, int symbolId)
    {
        var response = await WatchAsync(client, symbolId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
    }

    private static async Task<Row> RowAsync(HttpClient client, int id) =>
        (await client.GetFromJsonAsync<Row[]>(WatchUrl, Ct))!.Single(r => r.Id == id);

    [Fact]
    public async Task WithoutATokenEveryEndpointIsUnauthorized()
    {
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(WatchUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await WatchAsync(client, 1)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PutAsJsonAsync(EnabledUrl(1), new { enabled = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.DeleteAsync($"{WatchUrl}/1", Ct)).StatusCode);
    }

    [Fact]
    public async Task ASymbolWhoseMinuteCandlesAreCollectedCanBeWatchedAndShowsOnTheList()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        await CollectAsync(client, symbol);

        var id = await AddAsync(client, symbol);

        var row = await RowAsync(client, id);
        Assert.Equal((symbol, "Manual", true, true), (row.SymbolId, row.Reason, row.IsEnabled, row.HasMinuteCandles));
        Assert.Equal(ListingIds.Of(1, 2, symbol), row.SymbolListingId);
        Assert.False(string.IsNullOrWhiteSpace(row.ExchangeSymbol));
    }

    [Fact]
    public async Task ASymbolWhoseMinuteCandlesAreNotCollectedIsRefusedWithTheReason()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        await CollectAsync(client, symbol, OneHour);          // collected, but not at one minute

        var response = await WatchAsync(client, symbol);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<Problem>(Ct))!;
        Assert.Contains("1 minute candles", problem.Detail);
    }

    [Fact]
    public async Task AnUnknownListingIsRefused()
    {
        var client = await AdminAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(WatchUrl, new { symbolListingId = 999_999 }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(WatchUrl, new { symbolListingId = 0 }, Ct)).StatusCode);
    }

    [Fact]
    public async Task ASymbolAlreadyOnTheListIsAConflict()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        await CollectAsync(client, symbol);
        await AddAsync(client, symbol);

        Assert.Equal(HttpStatusCode.Conflict, (await WatchAsync(client, symbol)).StatusCode);
    }

    [Fact]
    public async Task TheWatchOfASymbolCanBeSwitchedOffAndOnAgain()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        await CollectAsync(client, symbol);
        var id = await AddAsync(client, symbol);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(EnabledUrl(id), new { enabled = false }, Ct)).StatusCode);
        Assert.False((await RowAsync(client, id)).IsEnabled);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(EnabledUrl(id), new { enabled = true }, Ct)).StatusCode);
        Assert.True((await RowAsync(client, id)).IsEnabled);
    }

    [Fact]
    public async Task SwitchingOffWorksWhenTheMinuteCandlesAreGoneButSwitchingOnDoesNot()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        var series = await CollectAsync(client, symbol);
        var id = await AddAsync(client, symbol);
        (await client.DeleteAsync(DeleteSeriesUrl(series), Ct)).EnsureSuccessStatusCode();

        Assert.False((await RowAsync(client, id)).HasMinuteCandles);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync(EnabledUrl(id), new { enabled = false }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(EnabledUrl(id), new { enabled = true }, Ct)).StatusCode);
        Assert.False((await RowAsync(client, id)).IsEnabled);
    }

    [Fact]
    public async Task ASymbolAddedByHandCanBeRemoved()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        await CollectAsync(client, symbol);
        var id = await AddAsync(client, symbol);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"{WatchUrl}/{id}", Ct)).StatusCode);

        Assert.DoesNotContain((await client.GetFromJsonAsync<Row[]>(WatchUrl, Ct))!, r => r.Id == id);
    }

    [Fact]
    public async Task ARowThatDoesNotExistIsNotFound()
    {
        var client = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync(EnabledUrl(999_999), new { enabled = true }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"{WatchUrl}/999999", Ct)).StatusCode);
    }

    [Fact]
    public async Task TakingASymbolOffTheListDoesNotStopItsCandlesBeingCollected()
    {
        var client = await AdminAsync();
        var symbol = NewSymbol();
        await CollectAsync(client, symbol);
        var id = await AddAsync(client, symbol);

        await client.DeleteAsync($"{WatchUrl}/{id}", Ct);

        // The collection is not the watch: the symbol can be put back at once because its candles are still being collected.
        Assert.Equal(HttpStatusCode.Created, (await WatchAsync(client, symbol)).StatusCode);
    }
}
