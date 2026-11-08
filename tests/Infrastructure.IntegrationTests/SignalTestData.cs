using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;

namespace Infrastructure.IntegrationTests;

/// <summary>A signal of one 4h candle (04:00 to 08:00 UTC), as the store and role tests need it.</summary>
internal static class SignalTestData
{
    public static readonly DateTime CandleOpen = new(2026, 11, 7, 4, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime Now = CandleOpen.AddHours(4).AddSeconds(2);

    public static TradeSignal Signal(
        PositionSideEnum side = PositionSideEnum.Long, string strategy = "S1", string symbol = "BTC/USDT", DateTime? candleOpen = null,
        decimal leverage = 5, TimeSpan? maxDuration = null, ExchangeEnum exchange = ExchangeEnum.Binance)
    {
        var open = candleOpen ?? CandleOpen;
        var (stop, target) = side == PositionSideEnum.Long ? (98m, 104m) : (102m, 96m);

        return new TradeSignal(
            SignalId.Build(strategy, symbol, TimeFrameEnum._4H, open, side, exchange), strategy, MarketTypeEnum.Futures, symbol, side, leverage,
            target, stop, 100m, TimeFrameEnum._4H, open, open.AddHours(4), open.AddHours(4).AddMinutes(45), maxDuration, exchange);
    }
}
