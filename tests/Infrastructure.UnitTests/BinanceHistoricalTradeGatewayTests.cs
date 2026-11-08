using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Exchange.Binance;

namespace Infrastructure.UnitTests;

/// <summary>What the trade history gateway refuses before any request is made (no client is needed to say no).</summary>
public class BinanceHistoricalTradeGatewayTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime Minute = new(2026, 11, 7, 8, 3, 0, DateTimeKind.Utc);
    private static readonly ExchangeSymbol Btc = new("BTCUSDT");

    private static BinanceHistoricalTradeGateway NewGateway() => new(client: null!, rateLimiters: null!);

    [Fact]
    public async Task AnotherExchangeIsRefused() =>
        await Assert.ThrowsAsync<ArgumentException>(() => NewGateway().GetTradesAsync((ExchangeEnum)99, MarketTypeEnum.Futures, Btc, Minute, Minute.AddMinutes(1), Ct));

    [Fact]
    public async Task SpotIsRefusedBecauseThisIsTheFuturesEndpoint() =>
        await Assert.ThrowsAsync<ArgumentException>(() => NewGateway().GetTradesAsync(ExchangeEnum.Binance, MarketTypeEnum.Spot, Btc, Minute, Minute.AddMinutes(1), Ct));

    [Fact]
    public async Task AnEmptyOrBackwardsWindowIsRefused()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NewGateway().GetTradesAsync(ExchangeEnum.Binance, MarketTypeEnum.Futures, Btc, Minute, Minute, Ct));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NewGateway().GetTradesAsync(ExchangeEnum.Binance, MarketTypeEnum.Futures, Btc, Minute, Minute.AddMinutes(-1), Ct));
    }

    [Fact]
    public async Task AWindowLongerThanAnHourIsRefusedBecauseBinanceAnswersNoMore() =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => NewGateway().GetTradesAsync(ExchangeEnum.Binance, MarketTypeEnum.Futures, Btc, Minute, Minute.AddHours(1).AddSeconds(1), Ct));
}
