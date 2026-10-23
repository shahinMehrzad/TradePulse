using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.UnitTests;

public class CandleSeriesTests
{
    [Fact]
    public void TheTableNameAndIntervalComeFromTheSeriesItself()
    {
        var series = new CandleSeries(ExchangeEnum.Binance, MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._4H);

        Assert.Equal("binance_futures_btcusdt_4h", series.TableName);
        Assert.Equal(TimeSpan.FromHours(4), series.Interval);
    }

    [Fact]
    public void ARowBecomesAPlanWithItsStartDateReadAsUtc()
    {
        var row = Row(exchange: (int)ExchangeEnum.Binance);

        Assert.True(CandleSeriesPlan.TryCreate(row, out var plan, out _));
        Assert.Equal(new CandleSeries(ExchangeEnum.Binance, MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H), plan.Series);
        Assert.Equal((new DateTime(2020, 1, 1), DateTimeKind.Utc), (plan.StartDate, plan.StartDate.Kind));
    }

    [Theory]
    [InlineData(99, (int)MarketTypeEnum.Futures, (int)SymbolEnum.BTCUSDT, (int)TimeFrameEnum._1H, "unknown exchange")]
    [InlineData((int)ExchangeEnum.Binance, 99, (int)SymbolEnum.BTCUSDT, (int)TimeFrameEnum._1H, "unknown market")]
    [InlineData((int)ExchangeEnum.Binance, (int)MarketTypeEnum.Futures, 999, (int)TimeFrameEnum._1H, "unknown symbol")]
    [InlineData((int)ExchangeEnum.Binance, (int)MarketTypeEnum.Futures, (int)SymbolEnum.BTCUSDT, 12345, "unknown time frame")]
    public void AnIdThatIsNoEnumMemberIsRefusedWithAReason(int exchange, int market, int symbol, int timeFrame, string expected)
    {
        Assert.False(CandleSeriesPlan.TryCreate(Row(exchange, market, symbol, timeFrame), out _, out var reason));
        Assert.Equal(expected, reason);
    }

    private static CandleWorkerConfiguration Row(
        int exchange, int market = (int)MarketTypeEnum.Futures, int symbol = (int)SymbolEnum.BTCUSDT, int timeFrame = (int)TimeFrameEnum._1H) => new()
    {
        ExchangeId = exchange,
        MarketId = market,
        SymbolId = symbol,
        TimeFrameId = timeFrame,
        StartDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
    };
}
