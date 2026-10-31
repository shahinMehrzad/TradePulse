using Application.Interfaces;
using Application.Queries.Candles;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.UnitTests;

public class CandleReaderTests
{
    private static readonly DateTime Start = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly CandleSeries Series = new(ExchangeEnum.Binance, MarketTypeEnum.Futures, SeededSymbol.BTCUSDT.Sym(), TimeFrameEnum._1H);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Candle CandleAt(int hour) => new(
        Start.AddHours(hour), 1m, 4m, 0.5m, 3m, 10m, Start.AddHours(hour + 1).AddMilliseconds(-1), 25m, 42, 6m, 15m);

    private static (CandleReader Reader, FakeCandleRepository Repository) Create(int stored)
    {
        var repository = new FakeCandleRepository();
        repository.Stored.AddRange(Enumerable.Range(0, stored).Select(CandleAt));
        return (new CandleReader(repository), repository);
    }

    [Fact]
    public async Task ReadsFromTheTableDerivedFromTheSeries()
    {
        var (reader, repository) = Create(3);

        await reader.GetRangeAsync(Series, null, null, 10, Ct);

        Assert.Equal("binance_futures_btcusdt_1h", repository.LastRead!.Value.TableName);
    }

    [Fact]
    public async Task LastPageHasNoCursor()
    {
        var (reader, _) = Create(3);

        var page = await reader.GetRangeAsync(Series, null, null, 10, Ct);

        Assert.Equal(3, page.Items.Count);
        Assert.Null(page.NextFrom);
    }

    [Fact]
    public async Task FullPageCarriesTheOpenTimeOfTheFirstRowOfTheNextPage()
    {
        var (reader, repository) = Create(5);

        var page = await reader.GetRangeAsync(Series, null, null, 3, Ct);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(Start.AddHours(3), page.NextFrom);
        Assert.Equal(4, repository.LastRead!.Value.Limit); // one extra row to detect the next page
    }

    [Fact]
    public async Task ExactlyFullLastPageHasNoCursor()
    {
        var (reader, _) = Create(3);

        var page = await reader.GetRangeAsync(Series, null, null, 3, Ct);

        Assert.Null(page.NextFrom);
    }

    [Fact]
    public async Task MapsEveryField()
    {
        var (reader, _) = Create(1);

        var item = Assert.Single((await reader.GetRangeAsync(Series, null, null, 10, Ct)).Items);

        Assert.Equal(Start, item.OpenTime);
        Assert.Equal(1m, item.Open);
        Assert.Equal(4m, item.High);
        Assert.Equal(0.5m, item.Low);
        Assert.Equal(3m, item.Close);
        Assert.Equal(10m, item.Volume);
        Assert.Equal(Start.AddHours(1).AddMilliseconds(-1), item.CloseTime);
        Assert.Equal(25m, item.QuoteAssetVolume);
        Assert.Equal(42, item.NumberOfTrades);
        Assert.Equal(6m, item.TakerBuyBaseVolume);
        Assert.Equal(15m, item.TakerBuyQuoteVolume);
    }

    [Fact]
    public async Task LatestAsksForExactlyTheRequestedLookback()
    {
        var (reader, repository) = Create(200);

        var candles = await reader.GetLatestAsync(Series, 78, Start.AddHours(150), Ct);

        Assert.Equal(78, candles.Count);
        Assert.Equal(78, repository.LastRead!.Value.Limit);
        Assert.Equal(Start.AddHours(150), repository.LastRead!.Value.To);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CandleReader.MaxLimit + 1)]
    public async Task RejectsLimitsOutsideTheAllowedRange(int limit)
    {
        var (reader, _) = Create(1);

        var range = await Assert.ThrowsAsync<InvalidCandleQueryException>(() => reader.GetRangeAsync(Series, null, null, limit, Ct));
        await Assert.ThrowsAsync<InvalidCandleQueryException>(() => reader.GetLatestAsync(Series, limit, null, Ct));
        Assert.Equal("limit", range.Parameter);
    }

    [Fact]
    public async Task RejectsAnInvertedRange()
    {
        var (reader, _) = Create(1);

        await Assert.ThrowsAsync<InvalidCandleQueryException>(() => reader.GetRangeAsync(Series, Start.AddHours(2), Start, 10, Ct));
    }
}
