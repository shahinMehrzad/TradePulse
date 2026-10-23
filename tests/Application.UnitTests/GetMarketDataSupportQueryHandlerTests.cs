using Application.Dtos.Lookups;
using Application.Queries.Lookups;
using Domain.Enums;
using Domain.Services;

namespace Application.UnitTests;

public class GetMarketDataSupportQueryHandlerTests
{
    private static async Task<IReadOnlyList<MarketDataSupportResponseDto>> HandleAsync() =>
        await new GetMarketDataSupportQueryHandler().HandleAsync(new GetMarketDataSupportQuery(), TestContext.Current.CancellationToken);

    private const int Binance = (int)ExchangeEnum.Binance;
    private const int Spot = (int)MarketTypeEnum.Spot;
    private const int Futures = (int)MarketTypeEnum.Futures;

    [Fact]
    public async Task SpotOffersCandlesOnlyAndFuturesOffersEverything()
    {
        var all = await HandleAsync();

        Assert.Equal(["Candle"], all.Where(s => s.MarketId == Spot).Select(s => s.DataType));
        Assert.Equal(["Candle", "FundingRate", "OpenInterest", "Liquidation"], all.Where(s => s.MarketId == Futures).Select(s => s.DataType));
        Assert.All(all, s => Assert.Equal(Binance, s.ExchangeId));
    }

    [Fact]
    public async Task OpenInterestListsItsTimeFramesAndFundingRateAndLiquidationHaveNone()
    {
        var futures = (await HandleAsync()).Where(s => s.MarketId == Futures).ToDictionary(s => s.DataType);

        Assert.Equal(
            MarketDataSupport.GetTimeFrames(ExchangeEnum.Binance, MarketTypeEnum.Futures, MarketDataTypeEnum.OpenInterest).Select(t => (int)t),
            futures["OpenInterest"].TimeFrameIds);
        Assert.Null(futures["FundingRate"].TimeFrameIds);
        Assert.Null(futures["Liquidation"].TimeFrameIds);
        Assert.Equal(Enum.GetValues<TimeFrameEnum>().Select(t => (int)t), futures["Candle"].TimeFrameIds);
    }

    [Fact]
    public async Task EveryEntryAgreesWithTheRuleItIsMadeFrom()
    {
        // The panel offers exactly what the admin API accepts: no entry may be one the rule refuses.
        foreach (var entry in await HandleAsync())
        {
            var dataType = Enum.Parse<MarketDataTypeEnum>(entry.DataType);

            if (entry.TimeFrameIds is null)
                Assert.True(MarketDataSupport.IsSupported((ExchangeEnum)entry.ExchangeId, (MarketTypeEnum)entry.MarketId, dataType));
            else
                Assert.All(entry.TimeFrameIds, t =>
                    Assert.True(MarketDataSupport.IsSupported((ExchangeEnum)entry.ExchangeId, (MarketTypeEnum)entry.MarketId, dataType, (TimeFrameEnum)t)));
        }
    }
}
