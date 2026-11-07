using System.Globalization;
using Domain.Enums;
using Domain.Extensions;

namespace Domain.Services;

/// <summary>
/// The one way a signal id is made, e.g. <c>S1:BTCUSDT:4h:2026-11-07T04:00Z:long</c>. The sender and the receiver both derive it from the
/// same five facts, and the receiver refuses a signal whose id differs: if each side read "candle open time" its own way, a repeated
/// signal would get a different id and be taken twice.
/// </summary>
public static class PaperSignalId
{
    public static string Build(string strategy, string symbol, TimeFrameEnum timeFrame, DateTime candleOpenTime, PositionSideEnum side) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{strategy}:{symbol.Replace("/", "").ToUpperInvariant()}:{timeFrame.ToRouteName()}:{candleOpenTime.AsUtc():yyyy-MM-dd'T'HH:mm'Z'}:{side.ToString().ToLowerInvariant()}");
}
