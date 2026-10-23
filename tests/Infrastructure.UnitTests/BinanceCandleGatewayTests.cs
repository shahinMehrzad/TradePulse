using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Exchange.Binance;

namespace Infrastructure.UnitTests;

public class BinanceCandleGatewayTests
{
    [Theory]
    [InlineData(MarketTypeEnum.Spot, 1000, 2)]
    [InlineData(MarketTypeEnum.Spot, 1, 2)]
    [InlineData(MarketTypeEnum.Futures, 99, 1)]
    [InlineData(MarketTypeEnum.Futures, 100, 2)]
    [InlineData(MarketTypeEnum.Futures, 499, 2)]
    [InlineData(MarketTypeEnum.Futures, 500, 5)]
    [InlineData(MarketTypeEnum.Futures, 1000, 5)]
    [InlineData(MarketTypeEnum.Futures, 1001, 10)]
    public void AKlineRequestCostsWhatBinanceChargesForItsSize(MarketTypeEnum market, int limit, int weight)
    {
        Assert.Equal(weight, BinanceCandleGateway.GetKlineWeight(market, limit));
    }

    [Fact]
    public void ThePageSizeTheCollectorsRelyOnCostsFiveOnFutures()
    {
        // The request budget (MaxLimit) is sized for full pages of this many candles.
        Assert.Equal(5, BinanceCandleGateway.GetKlineWeight(MarketTypeEnum.Futures, BinanceCandleGateway.MaxKlineLimit));
    }

    [Fact]
    public async Task AnotherExchangesSeriesIsRefusedBeforeAnyRequestIsMade()
    {
        // Fetching it from Binance would store the wrong prices under a right-looking table name. No client is needed to say no.
        var gateway = new BinanceCandleGateway(client: null!, rateLimiters: null!);
        var series = new CandleSeries((ExchangeEnum)99, MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            gateway.GetCandlesAsync(series, DateTime.UtcNow.AddDays(-1), to: null, TestContext.Current.CancellationToken));
    }
}
