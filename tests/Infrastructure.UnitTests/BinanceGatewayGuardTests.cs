using Application.Exchange;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Exchange.Binance;
using Microsoft.Extensions.Logging.Abstractions;

namespace Infrastructure.UnitTests;

/// <summary>
/// What a Binance gateway refuses before it makes any request, so no client is needed. Asking Binance for another exchange's
/// data, or for a period it does not have, would store wrong data under a right-looking name.
/// </summary>
public class BinanceGatewayGuardTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime From = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task TheFundingRateGatewayServesBinanceOnly()
    {
        var gateway = new BinanceFundingRateGateway(client: null!, rateLimiters: null!);

        await Assert.ThrowsAsync<ArgumentException>(() => gateway.GetFundingRatesAsync((ExchangeEnum)99, SymbolEnum.BTCUSDT, From, Ct));
    }

    [Fact]
    public async Task TheOpenInterestGatewayServesBinanceOnly()
    {
        var gateway = new BinanceOpenInterestGateway(client: null!, rateLimiters: null!);

        await Assert.ThrowsAsync<ArgumentException>(
            () => gateway.GetOpenInterestAsync((ExchangeEnum)99, SymbolEnum.BTCUSDT, TimeFrameEnum._1H, From, Ct));
    }

    [Theory]
    [InlineData(TimeFrameEnum._1S)]
    [InlineData(TimeFrameEnum._1M)]
    [InlineData(TimeFrameEnum._3M)]
    [InlineData(TimeFrameEnum._8H)]
    public async Task TheOpenInterestGatewayRefusesATimeFrameBinanceHasNoPeriodFor(TimeFrameEnum timeFrame)
    {
        var gateway = new BinanceOpenInterestGateway(client: null!, rateLimiters: null!);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => gateway.GetOpenInterestAsync(ExchangeEnum.Binance, SymbolEnum.BTCUSDT, timeFrame, From, Ct));
    }

    private sealed class NoListener : ILiveCandleListener, ILiquidationListener
    {
        public void OnMessage() { }
        public void OnClosedCandle(CandleSeries series, Candle candle) { }
        public void OnLiquidation(SymbolEnum symbol, Liquidation liquidation) { }
        public void OnConnectionLost() { }
        public void OnConnectionRestored(TimeSpan offlineFor) { }
    }

    [Fact]
    public async Task TheLiveCandleStreamServesBinanceOnly()
    {
        var stream = new BinanceLiveCandleStream(client: null!);
        var subscription = new LiveCandleSubscription((ExchangeEnum)99, MarketTypeEnum.Futures, TimeFrameEnum._1H, [SymbolEnum.BTCUSDT]);

        await Assert.ThrowsAsync<ArgumentException>(() => stream.SubscribeAsync(subscription, new NoListener(), Ct));
    }

    [Fact]
    public async Task TheLiquidationStreamServesBinanceOnly()
    {
        var stream = new BinanceLiquidationStream(client: null!, NullLogger<BinanceLiquidationStream>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => stream.SubscribeAsync((ExchangeEnum)99, [SymbolEnum.BTCUSDT], new NoListener(), Ct));
    }

    [Fact]
    public void BinanceKeepsAboutAMonthOfOpenInterestAndTheGatewayStaysInsideIt()
    {
        var maxHistory = new BinanceOpenInterestGateway(client: null!, rateLimiters: null!).MaxHistory;

        Assert.InRange(maxHistory, TimeSpan.FromDays(28), TimeSpan.FromDays(30));
    }
}
