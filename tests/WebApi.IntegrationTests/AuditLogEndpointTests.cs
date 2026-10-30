using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>What the admin changes is written to the audit log, with the signed-in user, and read back through OData.</summary>
public class AuditLogEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string AuditUrl = "/api/admin/audit-log";
    private const string ConfigUrl = "/api/admin/configurations";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Each test uses time frames / symbols of its own: the database is shared by the tests of this class.
    private static int _nextTimeFrame = 2;
    private static int _nextSymbol = 3;
    private static int NextTimeFrame() => new[] { 60, 180, 300, 900, 1800, 3600, 7200, 14400, 21600, 28800, 43200, 86400 }[Interlocked.Increment(ref _nextTimeFrame) % 12];
    private static int NextSymbol() => Interlocked.Increment(ref _nextSymbol) % 12 + 2;

    private record LoginResult(string AccessToken);
    private record Created(int Id);
    private record Me(string UserName);
    private record Entry(long Id, DateTime OccurredAt, string UserName, string Action, string EntityType, int EntityId, string Summary, string? Details);
    private record Page(int Count, Entry[] Value);

    private async Task<HttpClient> AdminAsync()
    {
        var client = api.CreateClient();
        var response = await api.AdminLoginAsync(client, Ct);
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await response.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken);
        return client;
    }

    private static async Task<int> AddSeriesAsync(HttpClient client, int timeFrameId, bool openInterest = false, string startDate = "2020-01-01T00:00:00Z")
    {
        var response = await client.PostAsJsonAsync($"{ConfigUrl}/add-config",
            new { exchangeId = 1, marketId = 2, symbolId = 1, timeFrameId, startDate, collectOpenInterest = openInterest }, Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
    }

    private static async Task<Entry[]> EntriesOfAsync(HttpClient client, string entityType, int entityId)
    {
        var filter = Uri.EscapeDataString($"entityType eq '{entityType}' and entityId eq {entityId}");
        var page = await client.GetFromJsonAsync<Page>($"{AuditUrl}?$filter={filter}&$orderby=occurredAt,id", Ct);
        return page!.Value;
    }

    [Fact]
    public async Task WithoutATokenTheLogIsUnauthorized()
    {
        var response = await api.CreateClient().GetAsync(AuditUrl, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AddingEditingAndRemovingASeriesAreEachLoggedWithTheUserAndWhatChanged()
    {
        var client = await AdminAsync();
        var userName = (await client.GetFromJsonAsync<Me>("/api/admin/me", Ct))!.UserName;
        var timeFrame = NextTimeFrame();

        var id = await AddSeriesAsync(client, timeFrame, openInterest: false);
        (await client.PutAsJsonAsync($"{ConfigUrl}/update-config",
            new { id, exchangeId = 1, marketId = 2, symbolId = 1, timeFrameId = timeFrame, startDate = "2021-06-01T00:00:00Z", collectOpenInterest = false }, Ct)).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"{ConfigUrl}/delete-config/{id}", Ct)).EnsureSuccessStatusCode();

        var entries = await EntriesOfAsync(client, "CandleSeries", id);

        Assert.Equal(["Added", "Updated", "Removed"], entries.Select(e => e.Action));
        Assert.All(entries, e =>
        {
            Assert.Equal(userName, e.UserName);
            Assert.StartsWith("binance / futures / btcusdt / ", e.Summary);
        });
        Assert.Equal("start date 2020-01-01, open interest off, market statistics off", entries[0].Details);
        Assert.Equal("start date 2020-01-01 -> 2021-06-01", entries[1].Details);
        Assert.Equal("start date 2021-06-01, open interest off, market statistics off", entries[2].Details);   // what the removed row held
    }

    [Fact]
    public async Task AnUpdateThatChangesNothingIsNotLogged()
    {
        var client = await AdminAsync();
        var timeFrame = NextTimeFrame();
        var id = await AddSeriesAsync(client, timeFrame);

        (await client.PutAsJsonAsync($"{ConfigUrl}/update-config",
            new { id, exchangeId = 1, marketId = 2, symbolId = 1, timeFrameId = timeFrame, startDate = "2020-01-01T00:00:00Z", collectOpenInterest = false }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(["Added"], (await EntriesOfAsync(client, "CandleSeries", id)).Select(e => e.Action));
    }

    [Fact]
    public async Task ARefusedChangeLeavesNoTrace()
    {
        var client = await AdminAsync();
        var before = (await client.GetFromJsonAsync<Page>($"{AuditUrl}?$top=1", Ct))!.Count;

        var response = await client.PostAsJsonAsync($"{ConfigUrl}/add-config",
            new { exchangeId = 1, marketId = 2, symbolId = 1, timeFrameId = NextTimeFrame(), startDate = "2999-01-01T00:00:00Z" }, Ct);   // in the future
        var missing = await client.DeleteAsync($"{ConfigUrl}/delete-config/987654", Ct);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound), (response.StatusCode, missing.StatusCode));
        Assert.Equal(before, (await client.GetFromJsonAsync<Page>($"{AuditUrl}?$top=1", Ct))!.Count);
    }

    [Fact]
    public async Task SymbolDataChangesAreLoggedToo()
    {
        var client = await AdminAsync();
        var symbol = NextSymbol();

        var created = await client.PostAsJsonAsync($"{ConfigUrl}/add-symbol-data",
            new { exchangeId = 1, marketId = 2, symbolId = symbol, collectFundingRate = false, collectLiquidation = true }, Ct);
        created.EnsureSuccessStatusCode();
        var id = (await created.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
        (await client.PutAsJsonAsync($"{ConfigUrl}/update-symbol-data",
            new { id, exchangeId = 1, marketId = 2, symbolId = symbol, collectFundingRate = true, fundingStartDate = "2022-02-02T00:00:00Z", collectLiquidation = true }, Ct)).EnsureSuccessStatusCode();
        (await client.DeleteAsync($"{ConfigUrl}/delete-symbol-data/{id}", Ct)).EnsureSuccessStatusCode();

        var entries = await EntriesOfAsync(client, "SymbolData", id);

        Assert.Equal(["Added", "Updated", "Removed"], entries.Select(e => e.Action));
        Assert.Equal("funding rate off, liquidations on", entries[0].Details);
        Assert.Equal("funding rate off -> on; funding start date none -> 2022-02-02", entries[1].Details);
        Assert.Equal("funding rate on from 2022-02-02, liquidations on", entries[2].Details);
    }

    [Fact]
    public async Task EveryFilterTheAuditPageSendsTogetherIsUnderstood()
    {
        // The exact shape TradePulseApiClient.AuditFilters builds (the panel has no test project of its own).
        var client = await AdminAsync();
        var id = await AddSeriesAsync(client, NextTimeFrame());
        var today = DateTime.UtcNow.Date;
        var filter = string.Join(" and ",
            "action eq 'Added'",
            "entityType eq 'CandleSeries'",
            "(contains(tolower(userName),'adm'))",
            "(contains(tolower(summary),'btcusdt') or contains(tolower(details),'open interest off'))",
            $"occurredAt ge {today:yyyy-MM-dd'T'HH:mm:ss'Z'}",
            $"occurredAt lt {today.AddDays(2):yyyy-MM-dd'T'HH:mm:ss'Z'}");

        var page = await client.GetFromJsonAsync<Page>(
            $"{AuditUrl}?$orderby=occurredAt desc,id desc&$top=25&$skip=0&$filter={Uri.EscapeDataString(filter)}", Ct);

        Assert.Contains(page!.Value, e => e.EntityId == id && e.Action == "Added");
        Assert.All(page.Value, e => Assert.Equal("Added", e.Action));
    }

    [Fact]
    public async Task TheLogCanBeFilteredByActionAndUserAndSortedNewestFirst()
    {
        var client = await AdminAsync();
        var id = await AddSeriesAsync(client, NextTimeFrame());
        (await client.DeleteAsync($"{ConfigUrl}/delete-config/{id}", Ct)).EnsureSuccessStatusCode();

        var removed = await client.GetFromJsonAsync<Page>(
            $"{AuditUrl}?$filter={Uri.EscapeDataString("action eq 'Removed' and entityType eq 'CandleSeries'")}&$orderby=occurredAt desc,id desc", Ct);
        var byOther = await client.GetFromJsonAsync<Page>(
            $"{AuditUrl}?$filter={Uri.EscapeDataString("userName eq 'somebody-else'")}", Ct);
        var recent = await client.GetFromJsonAsync<Page>(
            $"{AuditUrl}?$filter={Uri.EscapeDataString($"occurredAt ge {DateTime.UtcNow.AddMinutes(-5):yyyy-MM-dd'T'HH:mm:ss'Z'}")}&$top=100", Ct);

        Assert.Contains(removed!.Value, e => e.EntityId == id);
        Assert.Equal(removed.Value.OrderByDescending(e => e.OccurredAt).ThenByDescending(e => e.Id).Select(e => e.Id), removed.Value.Select(e => e.Id));
        Assert.Equal(0, byOther!.Count);
        Assert.Contains(recent!.Value, e => e.EntityId == id);   // dates in a filter are UTC, like the stored times
    }
}
