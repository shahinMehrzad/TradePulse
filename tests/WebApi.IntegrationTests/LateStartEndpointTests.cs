using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Repositories;

namespace WebApi.IntegrationTests;

/// <summary>
/// A symbol listed after the start date asked for has no earlier candles. The admin is told, and may move the start date to the first
/// candle; nothing changes unless they do. The database is shared by the tests of this class, so each test uses symbols of its own.
/// </summary>
public class LateStartEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private const string Root = "/api/admin/configurations";
    private const int FourHours = 14400;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record LoginResult(string AccessToken);
    private record Created(int Id);
    private record Late(int Id, DateTime StartDate, DateTime FirstCandle);
    private record AlignResult(int Aligned);
    private record Row(int Id, DateTime StartDate);
    private record Page(int Count, Row[] Value);
    private record Entry(string Action, int EntityId, string? Details);
    private record AuditPage(int Count, Entry[] Value);

    private static DateTime Utc(int year, int month, int day, int hour = 0) => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    private async Task<HttpClient> AdminAsync()
    {
        var client = api.CreateClient();
        var response = await api.AdminLoginAsync(client, Ct);
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", (await response.Content.ReadFromJsonAsync<LoginResult>(Ct))!.AccessToken);
        return client;
    }

    /// <summary>A futures 4h series asked for from <paramref name="startDate"/>, with candles from <paramref name="firstCandle"/> (none when null).</summary>
    private async Task<int> SeriesAsync(HttpClient client, SymbolEnum symbol, string startDate, DateTime? firstCandle)
    {
        var response = await client.PostAsJsonAsync($"{Root}/add-config",
            new { exchangeId = 1, marketId = 2, symbolId = (int)symbol, timeFrameId = FourHours, startDate }, Ct);
        response.EnsureSuccessStatusCode();
        var id = (await response.Content.ReadFromJsonAsync<Created>(Ct))!.Id;

        if (firstCandle is { } first)
        {
            var table = new Domain.ValueObjects.CandleSeries(ExchangeEnum.Binance, MarketTypeEnum.Futures, symbol, TimeFrameEnum._4H).TableName;
            var repository = new CandleRepository(api.ConnectionString);
            await repository.EnsureTableExistsAsync(table, Ct);
            await repository.BulkInsert(table,
                new[] { 0, 1 }.Select(i => first.AddHours(4 * i)).Select(open =>
                    new Candle(open, 1m, 4m, 0.5m, 3m, 10m, open.AddHours(4).AddMilliseconds(-1), 25m, 42, 6m, 15m)).ToList(), Ct);
        }

        return id;
    }

    private static async Task<Late[]> LateStartsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<Late[]>($"{Root}/candle-worker/late-starts", Ct))!;

    [Fact]
    public async Task WithoutATokenItIsUnauthorized()
    {
        var anonymous = api.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{Root}/candle-worker/late-starts", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"{Root}/candle-worker/align-starts", null, Ct)).StatusCode);
    }

    [Fact]
    public async Task ASeriesWhoseDataStartsOnALaterDayIsListedAndNothingChangesUntilTheAdminAsks()
    {
        var client = await AdminAsync();
        var id = await SeriesAsync(client, SymbolEnum.SOLUSDT, "2020-01-01T00:00:00Z", Utc(2023, 5, 3, 16));

        var late = Assert.Single(await LateStartsAsync(client), l => l.Id == id);

        Assert.Equal((Utc(2020, 1, 1), Utc(2023, 5, 3, 16)), (late.StartDate.ToUniversalTime(), late.FirstCandle.ToUniversalTime()));
        var list = await client.GetFromJsonAsync<Page>($"{Root}/candle-worker/odata?$filter=id eq {id}", Ct);
        Assert.Equal(new DateTime(2020, 1, 1), Assert.Single(list!.Value).StartDate.Date);   // looking did not change it
    }

    [Fact]
    public async Task AligningMovesTheStartDateToTheExactFirstCandleLogsItAndTheSeriesIsNoLongerListed()
    {
        var client = await AdminAsync();
        var id = await SeriesAsync(client, SymbolEnum.XRPUSDT, "2020-01-01T00:00:00Z", Utc(2023, 5, 3, 16));

        var response = await client.PostAsync($"{Root}/candle-worker/{id}/align-start", null, Ct);
        response.EnsureSuccessStatusCode();

        Assert.Equal(1, (await response.Content.ReadFromJsonAsync<AlignResult>(Ct))!.Aligned);
        var list = await client.GetFromJsonAsync<Page>($"{Root}/candle-worker/odata?$filter=id eq {id}", Ct);
        Assert.Equal(new DateTime(2023, 5, 3, 16, 0, 0), Assert.Single(list!.Value).StartDate);
        Assert.DoesNotContain(await LateStartsAsync(client), l => l.Id == id);

        var filter = Uri.EscapeDataString($"entityType eq 'CandleSeries' and entityId eq {id} and action eq 'Updated'");
        var audit = await client.GetFromJsonAsync<AuditPage>($"/api/admin/audit-log?$filter={filter}", Ct);
        Assert.Equal("start date 2020-01-01 -> 2023-05-03", Assert.Single(audit!.Value).Details);
    }

    [Fact]
    public async Task AFirstCandleLaterOnTheSameDayIsAListingInTheMiddleOfThatDayNotALateStart()
    {
        var client = await AdminAsync();
        var id = await SeriesAsync(client, SymbolEnum.BNBUSDT, "2023-05-03T00:00:00Z", Utc(2023, 5, 3, 16));

        Assert.DoesNotContain(await LateStartsAsync(client), l => l.Id == id);
    }

    [Fact]
    public async Task ASeriesWithoutAnyCandlesYetIsNotListed()
    {
        var client = await AdminAsync();
        var id = await SeriesAsync(client, SymbolEnum.SUIUSDT, "2020-01-01T00:00:00Z", firstCandle: null);   // not even a table

        Assert.DoesNotContain(await LateStartsAsync(client), l => l.Id == id);
    }

    [Fact]
    public async Task AligningASeriesThatIsNotLateIsRefusedAndAnUnknownOneIsNotFound()
    {
        var client = await AdminAsync();
        var onTime = await SeriesAsync(client, SymbolEnum.ADAUSDT, "2023-05-03T00:00:00Z", Utc(2023, 5, 3, 0));

        var notLate = await client.PostAsync($"{Root}/candle-worker/{onTime}/align-start", null, Ct);
        var unknown = await client.PostAsync($"{Root}/candle-worker/987654/align-start", null, Ct);

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.NotFound), (notLate.StatusCode, unknown.StatusCode));
    }

    [Fact]
    public async Task AligningAllChangesEveryLateSeriesAndLeavesTheOthersAlone()
    {
        var client = await AdminAsync();
        var late1 = await SeriesAsync(client, SymbolEnum.DOGEUSDT, "2020-01-01T00:00:00Z", Utc(2021, 3, 1, 8));
        var late2 = await SeriesAsync(client, SymbolEnum.LINKUSDT, "2020-01-01T00:00:00Z", Utc(2022, 7, 9, 12));
        var onTime = await SeriesAsync(client, SymbolEnum.LTCUSDT, "2024-01-01T00:00:00Z", Utc(2024, 1, 1, 0));

        var response = await client.PostAsync($"{Root}/candle-worker/align-starts", null, Ct);
        response.EnsureSuccessStatusCode();

        Assert.True((await response.Content.ReadFromJsonAsync<AlignResult>(Ct))!.Aligned >= 2);
        var after = await LateStartsAsync(client);
        Assert.DoesNotContain(after, l => l.Id == late1 || l.Id == late2);
        var untouched = await client.GetFromJsonAsync<Page>($"{Root}/candle-worker/odata?$filter=id eq {onTime}", Ct);
        Assert.Equal(new DateTime(2024, 1, 1), Assert.Single(untouched!.Value).StartDate);
    }
}
