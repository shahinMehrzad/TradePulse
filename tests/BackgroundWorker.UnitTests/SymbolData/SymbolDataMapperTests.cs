using BackgroundWorker.SymbolData;
using Domain.Entities;
using Domain.Enums;

namespace BackgroundWorker.UnitTests.SymbolData;

public class SymbolDataMapperTests
{
    private static SymbolDataConfiguration Row(
        int id = 1, int exchange = (int)ExchangeEnum.Binance, int market = (int)MarketTypeEnum.Futures, int symbol = (int)SymbolEnum.BTCUSDT,
        bool funding = true, bool liquidation = true) => new()
    {
        Id = id,
        ExchangeId = exchange,
        MarketId = market,
        SymbolId = symbol,
        CollectFundingRate = funding,
        FundingStartDate = funding ? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified) : null,
        CollectLiquidation = liquidation,
    };

    [Fact]
    public void IdsBecomeTheEnumMembersTheyAreAndTheDateIsReadAsUtc()
    {
        var plan = Assert.Single(SymbolDataMapper.ToPlans([Row(symbol: 3)]));

        Assert.Equal((ExchangeEnum.Binance, MarketTypeEnum.Futures, SymbolEnum.ETHUSDT, true, true),
            (plan.Exchange, plan.MarketType, plan.Symbol, plan.CollectFundingRate, plan.CollectLiquidation));
        Assert.Equal((new DateTime(2020, 1, 1), DateTimeKind.Utc), (plan.FundingStartDate!.Value, plan.FundingStartDate.Value.Kind));
    }

    [Fact]
    public void WithoutFundingRateThereIsNoDate()
    {
        var plan = Assert.Single(SymbolDataMapper.ToPlans([Row(funding: false)]));

        Assert.Null(plan.FundingStartDate);
    }

    [Theory]
    [InlineData(99, 2, 3, "unknown exchange")]
    [InlineData(1, 99, 3, "unknown market")]
    [InlineData(1, 2, 999, "unknown symbol")]
    public void ARowItCannotNameIsSkippedAndReportedNotFatal(int exchange, int market, int symbol, string reason)
    {
        var skipped = new List<(int Id, string Reason)>();

        var plans = SymbolDataMapper.ToPlans([Row(id: 2, exchange: exchange, market: market, symbol: symbol), Row(id: 1)], (row, why) => skipped.Add((row.Id, why)));

        Assert.Single(plans);
        Assert.Equal([(2, reason)], skipped);
    }

    [Fact]
    public void ASymbolListedTwiceIsKeptOnce()
    {
        Assert.Single(SymbolDataMapper.ToPlans([Row(id: 1), Row(id: 2)]));
    }

    [Fact]
    public void NoRowsMeansNoPlans()
    {
        Assert.Empty(SymbolDataMapper.ToPlans([]));
    }

    [Fact]
    public void EachExchangePicksItsOwnPlans()
    {
        var binance = SymbolDataTestSupport.Binance();
        var other = binance with { Exchange = (ExchangeEnum)2 };

        Assert.Equal([binance], new[] { binance, other }.ForExchange(ExchangeEnum.Binance));
    }
}
