using Domain.Enums;
using Infrastructure.Exchange.Binance;

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

    [Fact]
    public void BinanceKeepsAboutAMonthOfOpenInterestAndTheGatewayStaysInsideIt()
    {
        var maxHistory = new BinanceOpenInterestGateway(client: null!, rateLimiters: null!).MaxHistory;

        Assert.InRange(maxHistory, TimeSpan.FromDays(28), TimeSpan.FromDays(30));
    }
}
