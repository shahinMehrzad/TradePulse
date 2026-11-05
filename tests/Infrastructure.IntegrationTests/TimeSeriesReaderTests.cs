using Domain.Entities;
using Infrastructure.Repositories.TimeSeries;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class TimeSeriesReaderTests(PostgresFixture postgres)
{
    private static readonly DateTime Start = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewTable(string suffix) => $"binance_futures_test_{Guid.NewGuid():N}_{suffix}"[..44];

    private async Task<(TimeSeriesReader<LongShortRatio> Reader, string Table)> LongShortWithRowsAsync(int rows)
    {
        var writer = new TimeSeriesRepository<LongShortRatio>(postgres.ConnectionString, new LongShortRatioTable());
        var table = NewTable("longshortratio");
        await writer.EnsureTableExistsAsync(table, Ct);
        await writer.BulkInsert(table, Enumerable.Range(0, rows).Reverse().Select(i => new LongShortRatio(Start.AddHours(i), 1m + i, 0.6m, 0.4m)), Ct);
        return (new TimeSeriesReader<LongShortRatio>(postgres.ConnectionString, new LongShortRatioTable()), table);
    }

    [Fact]
    public async Task TheNewestRowsComeBackOldestFirstWhateverTheOrderTheyWereWritten()
    {
        var (reader, table) = await LongShortWithRowsAsync(10);

        var rows = await reader.GetNewestAsync(table, 4, Ct);

        Assert.Equal([7m, 8m, 9m, 10m], rows.Select(r => r.Ratio));
        Assert.Equal(Start.AddHours(6), rows[0].Time);
    }

    [Fact]
    public async Task ATableWithFewerRowsThanAskedGivesWhatItHasAndATimeIsUtc()
    {
        var (reader, table) = await LongShortWithRowsAsync(3);

        var rows = await reader.GetNewestAsync(table, 50, Ct);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(DateTimeKind.Utc, r.Time.Kind));   // the column has no zone: the reader says which one it is
        Assert.Equal((0.6m, 0.4m), (rows[2].LongAccount, rows[2].ShortAccount));
    }

    [Fact]
    public async Task AnEmptyTableOrACountOfZeroGivesNoRows()
    {
        var (reader, table) = await LongShortWithRowsAsync(0);
        Assert.Empty(await reader.GetNewestAsync(table, 5, Ct));

        var (filled, filledTable) = await LongShortWithRowsAsync(2);
        Assert.Empty(await filled.GetNewestAsync(filledTable, 0, Ct));
    }

    [Fact]
    public async Task ATableThatDoesNotExistThrowsSoItIsNeverTakenForEmpty()
    {
        var reader = new TimeSeriesReader<LongShortRatio>(postgres.ConnectionString, new LongShortRatioTable());

        await Assert.ThrowsAsync<PostgresException>(() => reader.GetNewestAsync(NewTable("missing"), 5, Ct));
    }

    [Fact]
    public async Task TheTakerVolumeTableIsReadBackToo()
    {
        var writer = new TimeSeriesRepository<TakerVolume>(postgres.ConnectionString, new TakerVolumeTable());
        var table = NewTable("takervolume");
        await writer.EnsureTableExistsAsync(table, Ct);
        await writer.BulkInsert(table, [new TakerVolume(Start, 1.25m, 500m, 400m)], Ct);

        var rows = await new TimeSeriesReader<TakerVolume>(postgres.ConnectionString, new TakerVolumeTable()).GetNewestAsync(table, 5, Ct);

        var row = Assert.Single(rows);
        Assert.Equal((Start, 1.25m, 500m, 400m), (row.Time, row.BuySellRatio, row.BuyVolume, row.SellVolume));
    }
}
