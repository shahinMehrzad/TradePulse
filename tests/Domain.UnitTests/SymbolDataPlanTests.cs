using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.UnitTests;

public class SymbolDataPlanTests
{
    private static SymbolDataConfiguration Row(int exchange = (int)ExchangeEnum.Binance, int market = (int)MarketTypeEnum.Futures, int symbol = (int)SymbolEnum.BTCUSDT) => new()
    {
        ExchangeId = exchange,
        MarketId = market,
        SymbolId = symbol,
        CollectFundingRate = true,
        FundingStartDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
        CollectLiquidation = true,
    };

    [Fact]
    public void ARowBecomesAPlanWithItsDateReadAsUtc()
    {
        Assert.True(SymbolDataPlan.TryCreate(Row(), out var plan, out _));

        Assert.Equal((ExchangeEnum.Binance, MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, true, true),
            (plan.Exchange, plan.MarketType, plan.Symbol, plan.CollectFundingRate, plan.CollectLiquidation));
        Assert.Equal((new DateTime(2020, 1, 1), DateTimeKind.Utc), (plan.FundingStartDate!.Value, plan.FundingStartDate.Value.Kind));
    }

    [Theory]
    [InlineData(99, (int)MarketTypeEnum.Futures, (int)SymbolEnum.BTCUSDT, "unknown exchange")]
    [InlineData((int)ExchangeEnum.Binance, 99, (int)SymbolEnum.BTCUSDT, "unknown market")]
    [InlineData((int)ExchangeEnum.Binance, (int)MarketTypeEnum.Futures, 999, "unknown symbol")]
    public void AnIdThatIsNoEnumMemberIsRefusedWithAReason(int exchange, int market, int symbol, string expected)
    {
        Assert.False(SymbolDataPlan.TryCreate(Row(exchange, market, symbol), out _, out var reason));
        Assert.Equal(expected, reason);
    }
}
