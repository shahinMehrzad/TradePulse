using Domain.Entities;
using Infrastructure.Repositories;
using Npgsql;

namespace Infrastructure.IntegrationTests;

[Collection(PostgresCollection.Name)]
public class CandleRepositoryTests(PostgresFixture postgres)
{
    private static readonly DateTime Start = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly CandleRepository _repository = new(postgres.ConnectionString);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Each test gets its own table so tests can run in any order.
    private static string NewTable() => $"binance_futures_test_{Guid.NewGuid():N}_1h"[..40];

    private static Candle CandleAt(DateTime openTime, decimal close = 100m) => new(
        openTime, openPrice: 99m, highPrice: 105m, lowPrice: 95m, closePrice: close, volume: 12.5m,
        closeTime: openTime + Hour - TimeSpan.FromMilliseconds(1),
        quoteAssetVolume: 1250.25m, numberOfTrades: 42, takerBuyBaseVolume: 6.25m, takerBuyQuoteVolume: 625.125m);

    private static IEnumerable<Candle> Series(int count) =>
        Enumerable.Range(0, count).Select(i => CandleAt(Start + i * Hour));

    [Fact]
    public async Task LatestOpenTimeIsNullForAnEmptyTable()
    {
        var table = NewTable();
        await _repository.EnsureTableExistsAsync(table, Ct);

        Assert.Null(await _repository.GetLatestOpenDataTime(table, Ct));
    }

    [Fact]
    public async Task LatestOpenTimeThrowsWhenTheTableDoesNotExist()
    {
        // Swallowing this would look like "empty table" and re-download the whole history.
        await Assert.ThrowsAsync<PostgresException>(() => _repository.GetLatestOpenDataTime(NewTable(), Ct));
    }

    [Fact]
    public async Task LatestOpenTimeThrowsWhenTheDatabaseIsUnreachable()
    {
        var unreachable = new CandleRepository("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=2");

        await Assert.ThrowsAnyAsync<Exception>(() => unreachable.GetLatestOpenDataTime(NewTable(), Ct));
    }

    [Fact]
    public async Task EnsureTableExistsIsIdempotent()
    {
        var table = NewTable();

        await _repository.EnsureTableExistsAsync(table, Ct);
        await _repository.EnsureTableExistsAsync(table, Ct);
    }

    [Fact]
    public async Task BulkInsertStoresEveryCandleAndLatestIsTheNewest()
    {
        var table = NewTable();
        await _repository.EnsureTableExistsAsync(table, Ct);

        var inserted = await _repository.BulkInsert(table, Series(10), Ct);

        Assert.Equal(10, inserted);
        var latest = await _repository.GetLatestOpenDataTime(table, Ct);
        Assert.Equal(Start + 9 * Hour, latest!.Value.UtcDateTime);
    }

    [Fact]
    public async Task RefetchingAnOverlappingRangeInsertsOnlyTheNewCandles()
    {
        var table = NewTable();
        await _repository.EnsureTableExistsAsync(table, Ct);
        await _repository.BulkInsert(table, Series(10), Ct);

        var inserted = await _repository.BulkInsert(table, Series(15), Ct);

        Assert.Equal(5, inserted);
        Assert.Equal(15, (await _repository.GetHistoryAsync(table, null, null, Ct)).Count());
    }

    [Fact]
    public async Task ExistingCandlesAreNotOverwritten()
    {
        // Documents the known limitation: DO NOTHING never picks up revised historical candles.
        var table = NewTable();
        await _repository.EnsureTableExistsAsync(table, Ct);
        await _repository.BulkInsert(table, [CandleAt(Start, close: 100m)], Ct);

        var inserted = await _repository.BulkInsert(table, [CandleAt(Start, close: 999m)], Ct);

        Assert.Equal(0, inserted);
        var stored = Assert.Single(await _repository.GetHistoryAsync(table, null, null, Ct));
        Assert.Equal(100m, stored.Close.Value);
    }

    [Fact]
    public async Task BulkInsertOfNothingIsHarmless()
    {
        var table = NewTable();
        await _repository.EnsureTableExistsAsync(table, Ct);

        Assert.Equal(0, await _repository.BulkInsert(table, [], Ct));
        Assert.Null(await _repository.GetLatestOpenDataTime(table, Ct));
    }

    [Fact]
    public async Task FailedBulkInsertRollsBackEverything()
    {
        var table = NewTable();
        await _repository.EnsureTableExistsAsync(table, Ct);

        // 100000000000 does not fit NUMERIC(18, 8): COPY into the temp table fails mid-batch.
        var bad = CandleAt(Start + Hour, close: 100_000_000_000m);

        await Assert.ThrowsAsync<PostgresException>(() => _repository.BulkInsert(table, [CandleAt(Start), bad], Ct));

        Assert.Null(await _repository.GetLatestOpenDataTime(table, Ct));
    }

    [Fact]
    public async Task HistoryRoundTripsEveryFieldAndReturnsUtc()
    {
        var table = NewTable();
        await _repository.EnsureTableExistsAsync(table, Ct);
        var original = CandleAt(Start);
        await _repository.BulkInsert(table, [original], Ct);

        var stored = Assert.Single(await _repository.GetHistoryAsync(table, null, null, Ct));

        Assert.Equal(original.TimeRange.OpenTime, stored.TimeRange.OpenTime);
        Assert.Equal(DateTimeKind.Utc, stored.TimeRange.OpenTime.Kind);
        Assert.Equal(original.TimeRange.CloseTime, stored.TimeRange.CloseTime);
        Assert.Equal(original.Open.Value, stored.Open.Value);
        Assert.Equal(original.High.Value, stored.High.Value);
        Assert.Equal(original.Low.Value, stored.Low.Value);
        Assert.Equal(original.Close.Value, stored.Close.Value);
        Assert.Equal(original.Volume, stored.Volume);
        Assert.Equal(original.QuoteAssetVolume, stored.QuoteAssetVolume);
        Assert.Equal(original.NumberOfTrades, stored.NumberOfTrades);
        Assert.Equal(original.TakerBuyBaseVolume, stored.TakerBuyBaseVolume);
        Assert.Equal(original.TakerBuyQuoteVolume, stored.TakerBuyQuoteVolume);
    }

    [Fact]
    public async Task HistoryFiltersByInclusiveRangeAndIsOrdered()
    {
        var table = NewTable();
        await _repository.EnsureTableExistsAsync(table, Ct);
        await _repository.BulkInsert(table, Series(10).Reverse(), Ct);

        var rows = (await _repository.GetHistoryAsync(table, Start + 2 * Hour, Start + 5 * Hour, Ct)).ToList();

        Assert.Equal([2, 3, 4, 5], rows.Select(c => (int)(c.TimeRange.OpenTime - Start).TotalHours));
    }

    [Fact]
    public async Task GapsAreReportedAsTheMissingCandleRange()
    {
        var table = NewTable();
        await _repository.EnsureTableExistsAsync(table, Ct);
        // Missing hours 3 and 4, then hour 8.
        var present = Enumerable.Range(0, 10).Where(i => i is not (3 or 4 or 8)).Select(i => CandleAt(Start + i * Hour));
        await _repository.BulkInsert(table, present, Ct);

        var gaps = await _repository.GetGapsAsync(table, Hour, null, Ct);

        Assert.Equal(2, gaps.Count);
        Assert.Equal(Start + 3 * Hour, gaps[0].FirstMissingOpenTime);
        Assert.Equal(Start + 4 * Hour, gaps[0].LastMissingOpenTime);
        Assert.Equal(Start + 8 * Hour, gaps[1].FirstMissingOpenTime);
        Assert.Equal(Start + 8 * Hour, gaps[1].LastMissingOpenTime);
    }

    [Fact]
    public async Task ContinuousSeriesHasNoGaps()
    {
        var table = NewTable();
        await _repository.EnsureTableExistsAsync(table, Ct);
        await _repository.BulkInsert(table, Series(20), Ct);

        Assert.Empty(await _repository.GetGapsAsync(table, Hour, null, Ct));
    }

    [Fact]
    public async Task MissingCandlesBeforeTheFirstRowAreReportedWhenAStartIsGiven()
    {
        var table = NewTable();
        await _repository.EnsureTableExistsAsync(table, Ct);
        await _repository.BulkInsert(table, Series(5).Skip(3), Ct); // starts at hour 3

        var gaps = await _repository.GetGapsAsync(table, Hour, Start, Ct);

        var gap = Assert.Single(gaps);
        Assert.Equal(Start, gap.FirstMissingOpenTime);
        Assert.Equal(Start + 2 * Hour, gap.LastMissingOpenTime);
        Assert.Empty(await _repository.GetGapsAsync(table, Hour, Start + 3 * Hour, Ct));
        Assert.Empty(await _repository.GetGapsAsync(table, Hour, Start + 3 * Hour - TimeSpan.FromMinutes(30), Ct));
    }

    [Fact]
    public async Task EmptyTableHasNoLeadingGap()
    {
        var table = NewTable();
        await _repository.EnsureTableExistsAsync(table, Ct);

        Assert.Empty(await _repository.GetGapsAsync(table, Hour, Start, Ct));
    }
}
