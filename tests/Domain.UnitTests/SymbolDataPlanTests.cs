using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Domain.UnitTests;

public class SymbolDataPlanTests
{
    private static SymbolDataConfiguration Row(
        int exchange = (int)ExchangeEnum.Binance, int market = (int)MarketTypeEnum.Futures, int symbol = (int)SeededSymbol.BTCUSDT,
        string exchangeSymbol = "BTCUSDT") => new()
    {
        SymbolListing = new SymbolListing { ExchangeId = exchange, MarketId = market, SymbolId = symbol, ExchangeSymbol = exchangeSymbol },
        CollectFundingRate = true,
        FundingStartDate = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
        CollectLiquidation = true,
    };

    [Fact]
    public void ARowBecomesAPlanWithItsDateReadAsUtc()
    {
        Assert.True(SymbolDataPlan.TryCreate(Row(), out var plan, out _));

        Assert.Equal((ExchangeEnum.Binance, MarketTypeEnum.Futures, SeededSymbol.BTCUSDT.Sym(), true, true),
            (plan.Exchange, plan.MarketType, plan.Symbol, plan.CollectFundingRate, plan.CollectLiquidation));
        Assert.Equal((new DateTime(2020, 1, 1), DateTimeKind.Utc), (plan.FundingStartDate!.Value, plan.FundingStartDate.Value.Kind));
    }

    [Theory]
    [InlineData(99, (int)MarketTypeEnum.Futures, (int)SeededSymbol.BTCUSDT, "unknown exchange")]
    [InlineData((int)ExchangeEnum.Binance, 99, (int)SeededSymbol.BTCUSDT, "unknown market")]
    public void AnIdThatIsNoEnumMemberIsRefusedWithAReason(int exchange, int market, int symbol, string expected)
    {
        Assert.False(SymbolDataPlan.TryCreate(Row(exchange, market, symbol), out _, out var reason));
        Assert.Equal(expected, reason);
    }

    [Fact]
    public void ASymbolThatIsNotListedOnTheMarketIsRefusedWithAReason()
    {
        var row = Row();
        row.SymbolListing = null;

        Assert.False(SymbolDataPlan.TryCreate(row, out _, out var reason));
        Assert.Equal("the symbol is not listed on this exchange market", reason);
    }

    [Theory]
    [InlineData("BTC USDT")]
    [InlineData("BTCUSDT; DROP TABLE x")]
    public void ANameThatCannotBePartOfATableNameIsRefusedWithAReason(string exchangeSymbol)
    {
        Assert.False(SymbolDataPlan.TryCreate(Row(exchangeSymbol: exchangeSymbol), out _, out var reason));
        Assert.StartsWith("unusable symbol name", reason);
    }
}
