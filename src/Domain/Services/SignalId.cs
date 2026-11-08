using System.Globalization;
using Domain.Enums;
using Domain.Extensions;

namespace Domain.Services;

/// <summary>
/// The one way a signal id is made, e.g. <c>S1:BTCUSDT:4h:2026-11-07T04:00Z:long</c>. The sender and the receiver both derive it from the
/// same five facts, and the receiver refuses a signal whose id differs: if each side read "candle open time" its own way, a repeated
/// signal would get a different id and be taken twice.
/// <para>
/// Binance, the first exchange, adds nothing to the id, so the ids that exist do not change. Any other exchange puts its name in front
/// (<c>okx:S1:BTCUSDT:...</c>): the same strategy, symbol and candle on two exchanges are two signals.
/// </para>
/// </summary>
public static class SignalId
{
    public static string Build(
        string strategy, string symbol, TimeFrameEnum timeFrame, DateTime candleOpenTime, PositionSideEnum side, ExchangeEnum exchange = ExchangeEnum.Binance) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{(exchange == ExchangeEnum.Binance ? "" : exchange.ToString().ToLowerInvariant() + ":")}{strategy}:{symbol.Replace("/", "").ToUpperInvariant()}:{timeFrame.ToRouteName()}:{candleOpenTime.AsUtc():yyyy-MM-dd'T'HH:mm'Z'}:{side.ToString().ToLowerInvariant()}");
}
