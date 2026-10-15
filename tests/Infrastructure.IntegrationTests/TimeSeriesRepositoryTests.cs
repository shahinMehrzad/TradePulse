using Domain.Entities;
using Infrastructure.Repositories.TimeSeries;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class TimeSeriesRepositoryTests(PostgresFixture postgres)
{
    private static readonly DateTime Start = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewTable(string suffix) => $"binance_futures_test_{Guid.NewGuid():N}_{suffix}"[..44];

    private TimeSeriesRepository<FundingRate> Funding() => new(postgres.ConnectionString, new FundingRateTable());

    private static FundingRate FundingAt(int index) => new(Start.AddHours(8 * index), 0.0001m * index, 50_000m + index);

    [Fact]
    public async Task LatestTimeIsNullForAnEmptyTableAndThrowsWhenMissing()
    {
        var repository = Funding();
        var table = NewTable("fundingrate");
        await repository.EnsureTableExistsAsync(table, Ct);

        Assert.Null(await repository.GetLatestTimeAsync(table, Ct));
        await Assert.ThrowsAsync<PostgresException>(() => repository.GetLatestTimeAsync(NewTable("missing"), Ct));
    }

    [Fact]
    public async Task FundingRateInsertIsIdempotentAndLatestIsUtc()
    {
        var repository = Funding();
        var table = NewTable("fundingrate");
        await repository.EnsureTableExistsAsync(table, Ct);

        Assert.Equal(5, await repository.BulkInsert(table, Enumerable.Range(0, 5).Select(FundingAt), Ct));
        Assert.Equal(2, await repository.BulkInsert(table, Enumerable.Range(3, 4).Select(FundingAt), Ct));

        var latest = await repository.GetLatestTimeAsync(table, Ct);
        Assert.Equal(FundingAt(6).FundingTime, latest);
        Assert.Equal(DateTimeKind.Utc, latest!.Value.Kind);
    }

    [Fact]
    public async Task OpenInterestRoundTrips()
    {
        var repository = new TimeSeriesRepository<OpenInterest>(postgres.ConnectionString, new OpenInterestTable());
        var table = NewTable("1h_openinterest");
        await repository.EnsureTableExistsAsync(table, Ct);

        var rows = Enumerable.Range(0, 3).Select(i => new OpenInterest(Start.AddHours(i), 1000m + i, 5_000_000m + i));

        Assert.Equal(3, await repository.BulkInsert(table, rows, Ct));
        Assert.Equal(Start.AddHours(2), await repository.GetLatestTimeAsync(table, Ct));
    }

    [Fact]
    public async Task LiquidationsWithTheSameTimestampAreAllKept()
    {
        // No primary key on purpose: the stream carries no unique id and several events can share a millisecond.
        var repository = new TimeSeriesRepository<Liquidation>(postgres.ConnectionString, new LiquidationTable());
        var table = NewTable("liquidation");
        await repository.EnsureTableExistsAsync(table, Ct);
        await repository.EnsureTableExistsAsync(table, Ct);

        var same = new Liquidation(Start, Domain.Enums.OrderSideEnum.Sell, "LIMIT", "IOC", "FILLED", 1m, 50_000m, 49_900m, 1m, 1m);

        Assert.Equal(2, await repository.BulkInsert(table, [same, same], Ct));
    }
}
