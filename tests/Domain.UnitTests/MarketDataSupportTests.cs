using Domain.Enums;
using Domain.Services;

namespace Domain.UnitTests;

public class MarketDataSupportTests
{
    private const ExchangeEnum Binance = ExchangeEnum.Binance;

    [Theory]
    [InlineData(MarketDataTypeEnum.Candle)]
    [InlineData(MarketDataTypeEnum.FundingRate)]
    [InlineData(MarketDataTypeEnum.OpenInterest)]
    [InlineData(MarketDataTypeEnum.Liquidation)]
    public void BinanceFuturesProvidesEveryKindOfData(MarketDataTypeEnum dataType)
    {
        Assert.True(MarketDataSupport.IsSupported(Binance, MarketTypeEnum.Futures, dataType, TimeFrameEnum._1H));
    }

    [Fact]
    public void BinanceSpotProvidesCandlesOnly()
    {
        Assert.True(MarketDataSupport.IsSupported(Binance, MarketTypeEnum.Spot, MarketDataTypeEnum.Candle, TimeFrameEnum._1H));

        foreach (var dataType in Enum.GetValues<MarketDataTypeEnum>().Where(d => d != MarketDataTypeEnum.Candle))
        {
            Assert.False(MarketDataSupport.TryValidate(Binance, MarketTypeEnum.Spot, dataType, TimeFrameEnum._1H, out var reason));
            Assert.Equal($"{dataType} is not available on Binance Spot", reason);
        }
    }

    [Theory]
    [InlineData(MarketTypeEnum.Spot)]
    [InlineData(MarketTypeEnum.Futures)]
    public void CandlesExistForEveryTimeFrame(MarketTypeEnum market)
    {
        Assert.All(Enum.GetValues<TimeFrameEnum>(), timeFrame =>
            Assert.True(MarketDataSupport.IsSupported(Binance, market, MarketDataTypeEnum.Candle, timeFrame)));
    }

    [Fact]
    public void OpenInterestExistsOnlyForTheTimeFramesBinanceHasStatisticsFor()
    {
        TimeFrameEnum[] supported = [TimeFrameEnum._5M, TimeFrameEnum._15M, TimeFrameEnum._30M, TimeFrameEnum._1H, TimeFrameEnum._2H,
            TimeFrameEnum._4H, TimeFrameEnum._6H, TimeFrameEnum._12H, TimeFrameEnum._1D];

        Assert.Equal(supported, MarketDataSupport.GetTimeFrames(Binance, MarketTypeEnum.Futures, MarketDataTypeEnum.OpenInterest));

        foreach (var timeFrame in Enum.GetValues<TimeFrameEnum>().Except(supported))
        {
            Assert.False(MarketDataSupport.TryValidate(Binance, MarketTypeEnum.Futures, MarketDataTypeEnum.OpenInterest, timeFrame, out var reason));
            Assert.Equal($"OpenInterest on Binance Futures does not support the {timeFrame.ToString().TrimStart('_')} time frame", reason);
        }
    }

    [Theory]
    [InlineData(MarketDataTypeEnum.Candle)]
    [InlineData(MarketDataTypeEnum.OpenInterest)]
    public void DataSplitByTimeFrameNeedsOne(MarketDataTypeEnum dataType)
    {
        Assert.False(MarketDataSupport.TryValidate(Binance, MarketTypeEnum.Futures, dataType, timeFrame: null, out var reason));
        Assert.Equal($"{dataType} needs a time frame", reason);
    }

    [Theory]
    [InlineData(MarketDataTypeEnum.FundingRate)]
    [InlineData(MarketDataTypeEnum.Liquidation)]
    public void DataWithoutATimeFrameNeitherNeedsNorMindsOne(MarketDataTypeEnum dataType)
    {
        Assert.True(MarketDataSupport.IsSupported(Binance, MarketTypeEnum.Futures, dataType));
        Assert.True(MarketDataSupport.IsSupported(Binance, MarketTypeEnum.Futures, dataType, TimeFrameEnum._8H));
        Assert.Empty(MarketDataSupport.GetTimeFrames(Binance, MarketTypeEnum.Futures, dataType));
    }

    [Fact]
    public void ValuesThatAreNoEnumMembersAreRefusedWithAReason()
    {
        Assert.False(MarketDataSupport.TryValidate((ExchangeEnum)99, MarketTypeEnum.Futures, MarketDataTypeEnum.Candle, TimeFrameEnum._1H, out var exchange));
        Assert.False(MarketDataSupport.TryValidate(Binance, (MarketTypeEnum)99, MarketDataTypeEnum.Candle, TimeFrameEnum._1H, out var market));
        Assert.False(MarketDataSupport.TryValidate(Binance, MarketTypeEnum.Futures, (MarketDataTypeEnum)99, TimeFrameEnum._1H, out var dataType));
        Assert.False(MarketDataSupport.TryValidate(Binance, MarketTypeEnum.Futures, MarketDataTypeEnum.Candle, (TimeFrameEnum)12345, out var timeFrame));

        Assert.Equal(["unknown exchange", "unknown market", "unknown data type", "unknown time frame"], [exchange, market, dataType, timeFrame]);
    }

    [Fact]
    public void ASupportedChoiceHasNoReason()
    {
        Assert.True(MarketDataSupport.TryValidate(Binance, MarketTypeEnum.Futures, MarketDataTypeEnum.OpenInterest, TimeFrameEnum._4H, out var reason));
        Assert.Empty(reason);
    }

    [Fact]
    public void AnUnavailableKindHasNoTimeFrames()
    {
        Assert.Empty(MarketDataSupport.GetTimeFrames(Binance, MarketTypeEnum.Spot, MarketDataTypeEnum.OpenInterest));
        Assert.Empty(MarketDataSupport.GetTimeFrames((ExchangeEnum)99, MarketTypeEnum.Futures, MarketDataTypeEnum.Candle));
    }

    [Fact]
    public void EveryDefinedExchangeMarketCombinationIsAnsweredWithoutThrowing()
    {
        // A new exchange or market added to an enum must never make the check throw, whether or not it has been given rules yet.
        foreach (var exchange in Enum.GetValues<ExchangeEnum>())
            foreach (var market in Enum.GetValues<MarketTypeEnum>())
                foreach (var dataType in Enum.GetValues<MarketDataTypeEnum>())
                    foreach (var timeFrame in Enum.GetValues<TimeFrameEnum>())
                        MarketDataSupport.TryValidate(exchange, market, dataType, timeFrame, out _);
    }
}
