using Domain.Entities;
using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;

namespace Domain.UnitTests;

/// <summary>One 4h candle (04:00 to 08:00 UTC) and the signals it produced, shared by the paper trade tests.</summary>
internal static class PaperTestData
{
    public static readonly DateTime CandleOpen = new(2026, 11, 7, 4, 0, 0, DateTimeKind.Utc);
    public static readonly DateTime CandleClose = CandleOpen.AddHours(4);

    /// <summary>Two seconds after the candle closed: when the signal reaches the receiver.</summary>
    public static readonly DateTime Now = CandleClose.AddSeconds(2);

    public static TradeSignal Long(decimal price = 100, decimal stop = 98, decimal target = 104, decimal leverage = 5, TimeSpan? maxDuration = null) =>
        Build(PositionSideEnum.Long, price, stop, target, leverage, maxDuration);

    public static TradeSignal Short(decimal price = 100, decimal stop = 102, decimal target = 96, decimal leverage = 5, TimeSpan? maxDuration = null) =>
        Build(PositionSideEnum.Short, price, stop, target, leverage, maxDuration);

    /// <summary>A signal with the id it must carry, valid for 45 minutes after its candle closed.</summary>
    private static TradeSignal Build(PositionSideEnum side, decimal price, decimal stop, decimal target, decimal leverage, TimeSpan? maxDuration) =>
        new(SignalId.Build("S1", "BTC/USDT", TimeFrameEnum._4H, CandleOpen, side),
            "S1", MarketTypeEnum.Futures, "BTC/USDT", side, leverage, target, stop, price,
            TimeFrameEnum._4H, CandleOpen, CandleClose, CandleClose.AddMinutes(45), maxDuration);

    /// <summary>The signal's trade, opened at its own price at <see cref="Now"/>.</summary>
    public static PaperTrade Opened(TradeSignal signal, decimal? entry = null) =>
        PaperTrade.Open(signal, entry ?? signal.SignalPrice, Now, openTradesAtEntry: 0);
}
