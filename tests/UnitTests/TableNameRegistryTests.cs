using Application.Services;
using Domain.Enums;

namespace UnitTests;

public class TableNameRegistryTests
{
    [Theory]
    [InlineData(MarketTypeEnum.Futures, TimeFrameEnum._4H, "binance_futures_btcusdt_4h")]
    [InlineData(MarketTypeEnum.Futures, TimeFrameEnum._15M, "binance_futures_btcusdt_15m")]
    [InlineData(MarketTypeEnum.Spot, TimeFrameEnum._1D, "binance_spot_btcusdt_1d")]
    public void GetCandleTableName_BuildsLowercaseName(MarketTypeEnum market, TimeFrameEnum timeframe, string expected)
    {
        var name = TableNameRegistry.GetCandleTableName(ExchangeEnum.Binance, market, SymbolEnum.BTCUSDT, timeframe);

        Assert.Equal(expected, name);
    }

    [Fact]
    public void GetCandleTableName_RejectsUnknownExchange()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TableNameRegistry.GetCandleTableName((ExchangeEnum)99, MarketTypeEnum.Spot, SymbolEnum.BTCUSDT, TimeFrameEnum._1H));

        Assert.Equal("exchange", ex.ParamName);
    }

    [Fact]
    public void GetCandleTableName_RejectsUnknownMarketType()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TableNameRegistry.GetCandleTableName(ExchangeEnum.Binance, (MarketTypeEnum)99, SymbolEnum.BTCUSDT, TimeFrameEnum._1H));

        Assert.Equal("marketType", ex.ParamName);
    }

    [Fact]
    public void GetCandleTableName_RejectsUnknownSymbol()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TableNameRegistry.GetCandleTableName(ExchangeEnum.Binance, MarketTypeEnum.Spot, (SymbolEnum)99, TimeFrameEnum._1H));

        Assert.Equal("symbol", ex.ParamName);
    }

    [Fact]
    public void GetCandleTableName_RejectsUnknownTimeframe()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            TableNameRegistry.GetCandleTableName(ExchangeEnum.Binance, MarketTypeEnum.Spot, SymbolEnum.BTCUSDT, (TimeFrameEnum)7));

        Assert.Equal("timeframe", ex.ParamName);
    }
}
