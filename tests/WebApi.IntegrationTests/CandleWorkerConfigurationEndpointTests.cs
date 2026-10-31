using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

public class CandleWorkerConfigurationEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string ListUrl = "/api/admin/configurations/candle-worker/odata";
    private const string AddUrl = "/api/admin/configurations/add-config";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Row(int Id, int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, DateTime StartDate, bool CollectOpenInterest = false);
    private record Created(int Id);
    private record Problem(int Status, string? Title, string? Detail);

    // Binance / Futures / BTCUSDT / 4h ... use values from the lookups; each test picks its own series because the database is shared.
    private static object Series(int symbolId, int timeFrameId, string? startDate = "2020-01-01T00:00:00", int exchangeId = 1, int marketId = 2) =>
        new { symbolListingId = ListingIds.Of(exchangeId, marketId, symbolId), timeFrameId, startDate };

    private async Task<HttpClient> AdminAsync()
    {
        var client = api.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new { Email = ApiFixture.AdminEmail, Username = "", Password = ApiFixture.AdminPassword }, Ct);
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private record Page(int Count, Row[] Value);

    // The biggest page the API allows: these tests look at the whole list (they add far fewer rows than that), the paging
    // itself is covered by CandleWorkerConfigurationODataTests.
    private static async Task<Row[]> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<Page>($"{ListUrl}?$top=100", Ct))!.Value;

    [Fact]
    public async Task WithoutATokenBothEndpointsAreUnauthorized()
    {
        var client = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ListUrl, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(AddUrl, Series(1, 3600), Ct)).StatusCode);
    }

    [Theory]
    [InlineData("2020-01-01T00:00:00Z", 60)]            // what a browser or any JSON client sends
    [InlineData("2020-01-01T00:00:00", 180)]
    [InlineData("2020-01-01T03:30:00+03:30", 300)]      // an offset: the same instant as midnight UTC
    public async Task AValidSeriesIsSavedAndItsIdIsReturned(string startDate, int timeFrameId)
    {
        var client = await AdminAsync();

        var response = await client.PostAsJsonAsync(AddUrl, Series(symbolId: 1, timeFrameId, startDate), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
        Assert.True(id > 0, "the new row's id should come back, not a row count or 0");

        var saved = Assert.Single(await ListAsync(client), r => r.Id == id);
        Assert.Equal((1, 2, 1, timeFrameId), (saved.ExchangeId, saved.MarketId, saved.SymbolId, saved.TimeFrameId));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0), saved.StartDate); // stored as UTC wall-clock time
    }

    [Fact]
    public async Task ASeriesAlreadyConfiguredIsAConflictNotAServerError()
    {
        var client = await AdminAsync();
        (await client.PostAsJsonAsync(AddUrl, Series(symbolId: 2, timeFrameId: 3600), Ct)).EnsureSuccessStatusCode();

        var again = await client.PostAsJsonAsync(AddUrl, Series(symbolId: 2, timeFrameId: 3600), Ct);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var problem = await again.Content.ReadFromJsonAsync<Problem>(Ct);
        Assert.Equal("Configuration already exists.", problem!.Detail);
        Assert.Single(await ListAsync(client), r => r.SymbolId == 2 && r.TimeFrameId == 3600);
    }

    [Theory]
    [MemberData(nameof(InvalidRequests))]
    public async Task AnInvalidRequestIsBadRequestWithAReasonAndNothingIsSaved(string case_, object body)
    {
        var client = await AdminAsync();
        var before = (await ListAsync(client)).Length;

        var response = await client.PostAsJsonAsync(AddUrl, body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{case_}: {(int)response.StatusCode} {text}");
        Assert.DoesNotContain("   at ", text);                    // no stack trace in the answer
        Assert.Equal(before, (await ListAsync(client)).Length);   // and no half-saved row
    }

    private const string UpdateUrl = "/api/admin/configurations/update-config";

    private async Task<int> AddAsync(HttpClient client, int symbolId, int timeFrameId)
    {
        var response = await client.PostAsJsonAsync(AddUrl, Series(symbolId, timeFrameId), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
    }

    private static object Edit(int id, int symbolId, int timeFrameId, string? startDate = "2021-06-01T00:00:00Z", int exchangeId = 1) =>
        new { id, symbolListingId = ListingIds.Of(exchangeId, 2, symbolId), timeFrameId, startDate };

    [Fact]
    public async Task UpdatingWithoutATokenIsUnauthorized()
    {
        var response = await api.CreateClient().PutAsJsonAsync(UpdateUrl, Edit(1, 1, 3600), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task UpdateChangesTheRowInPlaceIncludingItsStartDate()
    {
        var client = await AdminAsync();
        var id = await AddAsync(client, symbolId: 5, timeFrameId: 7200);
        var other = await AddAsync(client, symbolId: 7, timeFrameId: 7200);
        var rowsBefore = (await ListAsync(client)).Length;

        var response = await client.PutAsJsonAsync(UpdateUrl, Edit(id, symbolId: 6, timeFrameId: 21600), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = (await response.Content.ReadFromJsonAsync<Row>(Ct))!;
        var rows = await ListAsync(client);
        var saved = Assert.Single(rows, r => r.Id == id);
        Assert.Equal((saved.Id, saved.TimeFrameId, saved.StartDate), (returned.Id, returned.TimeFrameId, returned.StartDate));   // the answer is the stored row (with its listing nested)
        Assert.Equal((id, 1, 2, 6, 21600), (saved.Id, saved.ExchangeId, saved.MarketId, saved.SymbolId, saved.TimeFrameId));
        Assert.Equal(new DateTime(2021, 6, 1), saved.StartDate);   // not wiped, and the "Z" date was accepted
        Assert.Equal(rowsBefore, rows.Length);                     // an edit must never add a row
        var untouched = Assert.Single(rows, r => r.Id == other);
        Assert.Equal((7, 7200, new DateTime(2020, 1, 1)), (untouched.SymbolId, untouched.TimeFrameId, untouched.StartDate));
    }

    [Fact]
    public async Task UpdatingARowWithItsOwnValuesIsNotAConflictWithItself()
    {
        var client = await AdminAsync();
        var id = await AddAsync(client, symbolId: 8, timeFrameId: 7200);

        var response = await client.PutAsJsonAsync(UpdateUrl, Edit(id, symbolId: 8, timeFrameId: 7200), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task UpdatingToASeriesThatAlreadyExistsIsAConflictAndChangesNothing()
    {
        var client = await AdminAsync();
        var first = await AddAsync(client, symbolId: 3, timeFrameId: 1800);
        await AddAsync(client, symbolId: 4, timeFrameId: 1800);

        var response = await client.PutAsJsonAsync(UpdateUrl, Edit(first, symbolId: 4, timeFrameId: 1800), Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var unchanged = Assert.Single(await ListAsync(client), r => r.Id == first);
        Assert.Equal((3, new DateTime(2020, 1, 1)), (unchanged.SymbolId, unchanged.StartDate));
    }

    [Fact]
    public async Task UpdatingARowThatDoesNotExistIsNotFound()
    {
        var client = await AdminAsync();

        var response = await client.PutAsJsonAsync(UpdateUrl, Edit(id: 999_999, symbolId: 3, timeFrameId: 3600), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- Open interest flag. Symbols 12 to 14 are used by these cases only, so they cannot collide with the series above.

    private static object WithFlag(int marketId, int symbolId, int timeFrameId, bool collectOpenInterest) =>
        new { symbolListingId = ListingIds.Of(1, marketId, symbolId), timeFrameId, startDate = "2020-01-01T00:00:00", collectOpenInterest };

    private static object EditWithFlag(int id, int marketId, int symbolId, int timeFrameId, bool collectOpenInterest) =>
        new { id, symbolListingId = ListingIds.Of(1, marketId, symbolId), timeFrameId, startDate = "2021-06-01T00:00:00Z", collectOpenInterest };

    private async Task<int> AddWithFlagAsync(HttpClient client, int symbolId, int timeFrameId, bool collectOpenInterest)
    {
        var response = await client.PostAsJsonAsync(AddUrl, WithFlag(2, symbolId, timeFrameId, collectOpenInterest), Ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;
    }

    [Fact]
    public async Task AFuturesSeriesCanAskForOpenInterestAndTheFlagIsStored()
    {
        var client = await AdminAsync();

        var id = await AddWithFlagAsync(client, symbolId: 12, timeFrameId: 3600, collectOpenInterest: true);

        Assert.True(Assert.Single(await ListAsync(client), r => r.Id == id).CollectOpenInterest);
    }

    [Fact]
    public async Task WithoutTheFlagOpenInterestIsOff()
    {
        var client = await AdminAsync();

        var id = await AddAsync(client, symbolId: 12, timeFrameId: 14400);   // a client that does not know the field

        Assert.False(Assert.Single(await ListAsync(client), r => r.Id == id).CollectOpenInterest);
    }

    [Fact]
    public async Task ASpotSeriesWithoutOpenInterestIsFine()
    {
        var client = await AdminAsync();

        var response = await client.PostAsJsonAsync(AddUrl, WithFlag(marketId: 1, symbolId: 12, timeFrameId: 3600, collectOpenInterest: false), Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData(1, 3600, "OpenInterest is not available on Binance Spot")]
    [InlineData(2, 60, "does not support the 1M time frame")]
    [InlineData(2, 28800, "does not support the 8H time frame")]
    public async Task OpenInterestWhereTheMarketHasNoneIsRefusedWithTheReasonAndNothingIsSaved(int marketId, int timeFrameId, string reason)
    {
        var client = await AdminAsync();
        var before = (await ListAsync(client)).Length;

        var response = await client.PostAsJsonAsync(AddUrl, WithFlag(marketId, symbolId: 12, timeFrameId, collectOpenInterest: true), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, (await response.Content.ReadFromJsonAsync<Problem>(Ct))!.Detail);
        Assert.Equal(before, (await ListAsync(client)).Length);
    }

    [Fact]
    public async Task AnUpdateThatLeavesTheFlagOutSwitchesItOff()
    {
        var client = await AdminAsync();
        var id = await AddWithFlagAsync(client, symbolId: 13, timeFrameId: 3600, collectOpenInterest: true);

        // A client that does not know the field (the panel today) sends exactly this: an absent flag is false, like on add.
        var response = await client.PutAsJsonAsync(UpdateUrl, Edit(id, symbolId: 13, timeFrameId: 3600), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Assert.Single(await ListAsync(client), r => r.Id == id).CollectOpenInterest);
    }

    [Fact]
    public async Task AnUpdateCanSwitchOpenInterestOnAndOff()
    {
        var client = await AdminAsync();
        var id = await AddAsync(client, symbolId: 13, timeFrameId: 7200);

        (await client.PutAsJsonAsync(UpdateUrl, EditWithFlag(id, 2, 13, 7200, true), Ct)).EnsureSuccessStatusCode();
        Assert.True(Assert.Single(await ListAsync(client), r => r.Id == id).CollectOpenInterest);

        (await client.PutAsJsonAsync(UpdateUrl, EditWithFlag(id, 2, 13, 7200, false), Ct)).EnsureSuccessStatusCode();
        Assert.False(Assert.Single(await ListAsync(client), r => r.Id == id).CollectOpenInterest);
    }

    [Theory]
    [InlineData(2, 60, 3600)]      // a time frame Binance has no open interest for
    [InlineData(1, 14400, 7200)]   // Spot has none at all
    public async Task MovingASeriesWhereItsOpenInterestCannotExistIsRefusedAndTheRowIsUntouched(int marketId, int timeFrameId, int storedTimeFrameId)
    {
        var client = await AdminAsync();
        var id = await AddWithFlagAsync(client, symbolId: 14, timeFrameId: storedTimeFrameId, collectOpenInterest: true);
        var before = Assert.Single(await ListAsync(client), r => r.Id == id);

        // The flag stays on, yet the series it belongs to moves to a market or time frame without open interest.
        var response = await client.PutAsJsonAsync(UpdateUrl, EditWithFlag(id, marketId, 14, timeFrameId, collectOpenInterest: true), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, Assert.Single(await ListAsync(client), r => r.Id == id));
    }

    [Fact]
    public async Task MovingASeriesIsFineOnceTheFlagIsSwitchedOffInTheSameUpdate()
    {
        var client = await AdminAsync();
        var id = await AddWithFlagAsync(client, symbolId: 14, timeFrameId: 21600, collectOpenInterest: true);

        var response = await client.PutAsJsonAsync(UpdateUrl, EditWithFlag(id, 2, 14, 60, collectOpenInterest: false), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = Assert.Single(await ListAsync(client), r => r.Id == id);
        Assert.Equal((60, false), (saved.TimeFrameId, saved.CollectOpenInterest));
    }

    private static string DeleteUrl(int id) =>$"/api/admin/configurations/delete-config/{id}";

    [Fact]
    public async Task DeletingWithoutATokenIsUnauthorized()
    {
        var response = await api.CreateClient().DeleteAsync(DeleteUrl(1), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteRemovesOnlyThatRowAndAnswersNoContent()
    {
        var client = await AdminAsync();
        var doomed = await AddAsync(client, symbolId: 4, timeFrameId: 300);
        var survivor = await AddAsync(client, symbolId: 4, timeFrameId: 900);

        var response = await client.DeleteAsync(DeleteUrl(doomed), Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsStringAsync(Ct));
        var rows = await ListAsync(client);
        Assert.DoesNotContain(rows, r => r.Id == doomed);
        Assert.Contains(rows, r => r.Id == survivor);
    }

    [Fact]
    public async Task DeletingTheSameRowTwiceIsNotFoundTheSecondTime()
    {
        var client = await AdminAsync();
        var id = await AddAsync(client, symbolId: 4, timeFrameId: 14400);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(DeleteUrl(id), Ct)).StatusCode);

        var again = await client.DeleteAsync(DeleteUrl(id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.DoesNotContain("   at ", await again.Content.ReadAsStringAsync(Ct));
    }

    [Theory]
    [InlineData(999_999)]
    [InlineData(-1)]
    public async Task DeletingARowThatDoesNotExistIsNotFoundAndChangesNothing(int id)
    {
        var client = await AdminAsync();
        var before = (await ListAsync(client)).Length;

        var response = await client.DeleteAsync(DeleteUrl(id), Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(before, (await ListAsync(client)).Length);
    }

    [Fact]
    public async Task DeletingIdZeroIsBadRequest()
    {
        var client = await AdminAsync();

        var response = await client.DeleteAsync(DeleteUrl(0), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ADeletedSeriesCanBeAddedAgain()
    {
        var client = await AdminAsync();
        var id = await AddAsync(client, symbolId: 3, timeFrameId: 86400);
        (await client.DeleteAsync(DeleteUrl(id), Ct)).EnsureSuccessStatusCode();

        // The unique index no longer holds the series, so it is not a conflict any more.
        var newId = await AddAsync(client, symbolId: 3, timeFrameId: 86400);

        Assert.NotEqual(id, newId);
    }

    // The database is shared by the tests of this class, so every case adds a series of its own (symbol 8, one time frame per case).
    private static readonly int[] CaseTimeFrames = [1, 60, 900, 3600, 14400, 28800, 86400];

    [Theory]
    [MemberData(nameof(InvalidEdits))]
    public async Task AnInvalidEditIsBadRequestAndTheRowKeepsItsValues(string case_, int slot, object body)
    {
        var client = await AdminAsync();
        var id = await AddAsync(client, symbolId: 8, timeFrameId: CaseTimeFrames[slot]);
        var before = Assert.Single(await ListAsync(client), r => r.Id == id);

        var response = await client.PutAsJsonAsync(UpdateUrl, body is Func<int, object> make ? make(id) : body, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{case_}: {(int)response.StatusCode} {text}");
        Assert.DoesNotContain("   at ", text);
        Assert.Equal(before, Assert.Single(await ListAsync(client), r => r.Id == id));
    }

    public static TheoryData<string, int, object> InvalidEdits() => new()
    {
        { "no id", 0, (Func<int, object>)(_ => Edit(0, 8, 2)) },
        { "unknown exchange", 1, (Func<int, object>)(id => Edit(id, 8, 7200, exchangeId: 99)) },
        { "unknown symbol", 2, (Func<int, object>)(id => Edit(id, 999, 7200)) },
        { "unknown time frame", 3, (Func<int, object>)(id => Edit(id, 8, 12345)) },
        { "symbol missing", 4, (Func<int, object>)(id => Edit(id, 0, 7200)) },
        { "no start date", 5, (Func<int, object>)(id => Edit(id, 8, 7200, startDate: null)) },
        { "start date in the future", 6, (Func<int, object>)(id => Edit(id, 8, 7200, startDate: "2999-01-01T00:00:00")) },
    };

    public static TheoryData<string, object> InvalidRequests() => new()
    {
        { "exchange missing", Series(1, 900, exchangeId: 0) },
        { "market missing", Series(1, 900, marketId: 0) },
        { "symbol missing", Series(0, 900) },
        { "time frame missing", Series(1, 0) },
        { "unknown exchange", Series(1, 900, exchangeId: 99) },
        { "unknown symbol", Series(999, 900) },
        { "unknown time frame", Series(1, 12345) },
        { "no start date", Series(1, 900, startDate: null) },
        { "start date in the future", Series(1, 900, startDate: "2999-01-01T00:00:00") },
    };
}
