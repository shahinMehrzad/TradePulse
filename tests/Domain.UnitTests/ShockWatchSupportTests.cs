using Domain.Entities;
using Domain.Enums;
using Domain.Services;

namespace Domain.UnitTests;

public class ShockWatchSupportTests
{
    [Theory]
    [InlineData(ExchangeEnum.Binance, MarketTypeEnum.Futures, true)]
    [InlineData(ExchangeEnum.Binance, MarketTypeEnum.Spot, true)]
    [InlineData(ExchangeEnum.Binance, MarketTypeEnum.Cfd, false)]
    [InlineData(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, false)]
    [InlineData(ExchangeEnum.Pepperstone, MarketTypeEnum.Futures, false)]
    public void OnlyBinanceHasTheTradesTheWatchJudges(ExchangeEnum exchange, MarketTypeEnum market, bool supported)
    {
        Assert.Equal(supported, ShockWatchSupport.IsSupported(exchange, market));
    }

    [Fact]
    public void AListingWithIdsThatAreNoMembersOfTheEnumsIsNotSupportedAndDoesNotThrow()
    {
        var odd = new SymbolListing { Id = 1, ExchangeId = 99, MarketId = 2, SymbolId = 1, ExchangeSymbol = "X" };

        Assert.False(ShockWatchSupport.IsSupported(odd));
    }

    [Fact]
    public void TheRefusalNamesTheSymbolAndTheReason()
    {
        var gold = new SymbolListing { Id = 40, ExchangeId = (int)ExchangeEnum.Pepperstone, MarketId = (int)MarketTypeEnum.Cfd, SymbolId = 40, ExchangeSymbol = "XAUUSD" };

        var text = ShockWatchSupport.Refusal(gold);

        Assert.Contains("XAUUSD", text);
        Assert.Contains("only Binance has", text);
    }
}
