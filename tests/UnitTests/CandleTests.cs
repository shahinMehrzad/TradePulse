using Domain.Entities;

namespace UnitTests;

public class CandleTests
{
    private static readonly DateTime Open = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Constructor_MapsAllFields()
    {
        var candle = new Candle(
            openTime: Open,
            openPrice: 100m,
            highPrice: 110m,
            lowPrice: 95m,
            closePrice: 105m,
            volume: 12.5m,
            closeTime: Open.AddHours(4),
            quoteAssetVolume: 1300m,
            numberOfTrades: 321,
            takerBuyBaseVolume: 6m,
            takerBuyQuoteVolume: 640m);

        Assert.Equal(Open, candle.TimeRange.OpenTime);
        Assert.Equal(Open.AddHours(4), candle.TimeRange.CloseTime);
        Assert.Equal(100m, candle.Open.Value);
        Assert.Equal(110m, candle.High.Value);
        Assert.Equal(95m, candle.Low.Value);
        Assert.Equal(105m, candle.Close.Value);
        Assert.Equal(12.5m, candle.Volume);
        Assert.Equal(1300m, candle.QuoteAssetVolume);
        Assert.Equal(321, candle.NumberOfTrades);
        Assert.Equal(6m, candle.TakerBuyBaseVolume);
        Assert.Equal(640m, candle.TakerBuyQuoteVolume);
    }

    [Fact]
    public void Constructor_RejectsNegativePrice()
    {
        Assert.Throws<ArgumentException>(() => new Candle(Open, -1m, 1m, 1m, 1m, 1m, Open.AddHours(1), 1m, 1, 1m, 1m));
    }

    [Fact]
    public void Constructor_RejectsInvalidTimeRange()
    {
        Assert.Throws<ArgumentException>(() => new Candle(Open, 1m, 1m, 1m, 1m, 1m, Open.AddHours(-1), 1m, 1, 1m, 1m));
    }
}
