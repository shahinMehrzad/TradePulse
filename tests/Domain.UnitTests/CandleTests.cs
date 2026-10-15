using Domain.Entities;

namespace Domain.UnitTests;

public class CandleTests
{
    private static readonly DateTime Open = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Constructor_MapsEveryField()
    {
        var close = Open.AddHours(1).AddMilliseconds(-1);

        var candle = new Candle(
            openTime: Open, openPrice: 1m, highPrice: 4m, lowPrice: 0.5m, closePrice: 3m,
            volume: 10m, closeTime: close, quoteAssetVolume: 25m,
            numberOfTrades: 42, takerBuyBaseVolume: 6m, takerBuyQuoteVolume: 15m);

        Assert.Equal(Open, candle.TimeRange.OpenTime);
        Assert.Equal(close, candle.TimeRange.CloseTime);
        Assert.Equal(1m, candle.Open.Value);
        Assert.Equal(4m, candle.High.Value);
        Assert.Equal(0.5m, candle.Low.Value);
        Assert.Equal(3m, candle.Close.Value);
        Assert.Equal(10m, candle.Volume);
        Assert.Equal(25m, candle.QuoteAssetVolume);
        Assert.Equal(42, candle.NumberOfTrades);
        Assert.Equal(6m, candle.TakerBuyBaseVolume);
        Assert.Equal(15m, candle.TakerBuyQuoteVolume);
    }

    [Fact]
    public void Constructor_RejectsNegativePrices()
    {
        Assert.Throws<ArgumentException>(() => new Candle(
            Open, -1m, 4m, 0.5m, 3m, 10m, Open.AddHours(1), 25m, 42, 6m, 15m));
    }

    [Fact]
    public void Constructor_RejectsInvalidTimeRange()
    {
        Assert.Throws<ArgumentException>(() => new Candle(
            Open, 1m, 4m, 0.5m, 3m, 10m, Open.AddHours(-1), 25m, 42, 6m, 15m));
    }
}
