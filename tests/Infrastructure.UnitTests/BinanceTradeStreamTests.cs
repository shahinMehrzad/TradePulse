using Binance.Net.Objects.Models.Spot.Socket;
using Domain.ValueObjects;
using Infrastructure.Exchange.Binance;

namespace Infrastructure.UnitTests;

public class BinanceTradeStreamTests
{
    private static readonly Dictionary<string, ExchangeSymbol> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["BTCUSDT"] = new ExchangeSymbol("BTCUSDT"),
    };

    private static BinanceStreamAggregatedTrade Trade(string symbol = "BTCUSDT") => new()
    {
        Symbol = symbol,
        Price = 79893.4m,
        Quantity = 0.125m,
        TradeTime = new DateTime(2026, 10, 29, 20, 34, 15, 250, DateTimeKind.Unspecified),
    };

    [Fact]
    public void ATradeOfAWatchedSymbolBecomesALiveTradeInUtc()
    {
        Assert.True(BinanceTradeStream.TryMap(Known, Trade(), out var symbol, out var trade));

        Assert.Equal("BTCUSDT", symbol.Value);
        Assert.Equal(79893.4m, trade.Price);
        Assert.Equal(0.125m, trade.Quantity);
        Assert.Equal(new DateTime(2026, 10, 29, 20, 34, 15, 250, DateTimeKind.Utc), trade.Time);
        Assert.Equal(DateTimeKind.Utc, trade.Time.Kind);
    }

    [Fact]
    public void TheSymbolIsFoundInAnyCase()
    {
        Assert.True(BinanceTradeStream.TryMap(Known, Trade("btcusdt"), out var symbol, out _));
        Assert.Equal("BTCUSDT", symbol.Value);
    }

    [Fact]
    public void ATradeOfASymbolNotAskedForIsLeftOut()
    {
        Assert.False(BinanceTradeStream.TryMap(Known, Trade("ETHUSDT"), out _, out var trade));
        Assert.Equal(default, trade);
    }
}
