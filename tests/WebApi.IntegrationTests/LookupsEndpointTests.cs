using System.Net;
using System.Net.Http.Json;

namespace WebApi.IntegrationTests;

/// <summary>The lookup lists are public on purpose (they only mirror the enums), so none of these calls carries a token.</summary>
public class LookupsEndpointTests(ApiFixture api) : IClassFixture<ApiFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private record Lookup(int Id, string Name);
    private record TimeFrame(int Id, string Name, int IntervalToSeconds);

    private async Task<T[]> GetAsync<T>(string url)
    {
        var response = await api.CreateClient().GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T[]>(Ct))!;
    }

    [Fact]
    public async Task ExchangesAndMarketsAreListedWithoutAToken()
    {
        Assert.Equal(["Binance"], (await GetAsync<Lookup>("/api/lookups/exchanges")).Select(x => x.Name));
        Assert.Equal(["Spot", "Futures"], (await GetAsync<Lookup>("/api/lookups/markets")).OrderBy(x => x.Id).Select(x => x.Name));
    }

    [Fact]
    public async Task SymbolsCanBeSearchedByNameIgnoringCase()
    {
        var all = await GetAsync<Lookup>("/api/lookups/symbols");
        var found = await GetAsync<Lookup>("/api/lookups/symbols?keyword=BtC");
        var none = await GetAsync<Lookup>("/api/lookups/symbols?keyword=nothing-like-this");

        Assert.Contains(all, x => x.Name == "BTCUSDT");
        Assert.Equal(all.OrderBy(x => x.Name).Select(x => x.Name), all.Select(x => x.Name));
        Assert.Equal(["BTCUSDT"], found.Select(x => x.Name));
        Assert.Empty(none);
    }

    [Fact]
    public async Task TimeFramesCanBeFilteredByAnIntervalRange()
    {
        var all = await GetAsync<TimeFrame>("/api/lookups/time-frames");
        var range = await GetAsync<TimeFrame>("/api/lookups/time-frames?minInterval=3600&maxInterval=14400");
        var onlyMax = await GetAsync<TimeFrame>("/api/lookups/time-frames?maxInterval=900");
        var onlyMin = await GetAsync<TimeFrame>("/api/lookups/time-frames?minInterval=86400");

        Assert.Equal(all.OrderBy(x => x.IntervalToSeconds).Select(x => x.Id), all.Select(x => x.Id));
        Assert.Equal(["_1H", "_2H", "_4H"], range.Select(x => x.Name));
        Assert.Equal(["_1S", "_1M", "_3M", "_5M", "_15M"], onlyMax.Select(x => x.Name));
        Assert.Equal(["_1D"], onlyMin.Select(x => x.Name));
    }

    [Fact]
    public async Task TheConfigSchemaLivesInTheTestDatabaseNotInTheLocalOne()
    {
        // Guards ApiFixture itself: without ConfigConnection the host would migrate and read the developer's own database.
        // The app migrates the database when its host starts; do not rely on another test having started it first.
        using var _ = api.CreateClient();

        await using var connection = new Npgsql.NpgsqlConnection(api.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new Npgsql.NpgsqlCommand("select count(*) from config.exchanges", connection);

        Assert.Equal(1L, await command.ExecuteScalarAsync(Ct));
    }
}
