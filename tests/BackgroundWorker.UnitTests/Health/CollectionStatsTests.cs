using BackgroundWorker.Health;
using Domain.Enums;

namespace BackgroundWorker.UnitTests.Health;

public class CollectionStatsTests
{
    [Fact]
    public void ItCountsWhatEachKindOfDataStoredAndStartsOverAfterBeingRead()
    {
        var stats = new CollectionStats();
        stats.Stored(MarketDataTypeEnum.Candle, 10);
        stats.Stored(MarketDataTypeEnum.Candle, 5);
        stats.Stored(MarketDataTypeEnum.FundingRate, 2);
        stats.Stored(MarketDataTypeEnum.OpenInterest, 3);
        stats.Stored(MarketDataTypeEnum.Liquidation, 4);

        Assert.Equal(new StoredCounts(15, 2, 3, 4), stats.TakeSinceLastCall());
        Assert.Equal(new StoredCounts(0, 0, 0, 0), stats.TakeSinceLastCall());
    }

    [Fact]
    public void RowsThatWereNotNewAreNotCounted()
    {
        // Fetching a range the table already has stores nothing; that must not look like progress.
        var stats = new CollectionStats();

        stats.Stored(MarketDataTypeEnum.Candle, 0);
        stats.Stored(MarketDataTypeEnum.Candle, -1);

        Assert.Equal(0, stats.TakeSinceLastCall().Candles);
    }

    [Fact]
    public async Task ManyWritersAtOnceLoseNothing()
    {
        var stats = new CollectionStats();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 10_000; i++)
                stats.Stored(MarketDataTypeEnum.Candle, 1);
        }, TestContext.Current.CancellationToken)));

        Assert.Equal(80_000, stats.TakeSinceLastCall().Candles);
    }
}
