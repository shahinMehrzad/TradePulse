using System.Globalization;
using Application.Notifications;
using Domain.Entities;
using Domain.Enums;

namespace Application.Research.Watch;

/// <summary>
/// The words of a shock alert. It says what the price did, never what it will do and never what to do: after such a move the price
/// goes on the same way only about as often as it turns back.
/// </summary>
public static class ShockAlertMessage
{
    public static Alert Create(ShockAlert alert, string symbol, ShockSignalKind kind)
    {
        var culture = CultureInfo.InvariantCulture;
        var level = alert.Level == AlertLevelEnum.Alarm ? "ALARM" : "Attention";
        var verb = alert.Direction == EventDirectionEnum.Up ? "rose" : "fell";
        var stage = kind switch
        {
            ShockSignalKind.Started => string.Empty,
            ShockSignalKind.Escalated => " (raised)",
            _ => " (over)",
        };

        var title = string.Create(culture, $"{level}: {symbol} {verb} {Math.Abs(alert.Move) * 100:0.00} % in {alert.WindowSeconds} s{stage}");

        var body = string.Join(Environment.NewLine,
            string.Create(culture, $"Since: {alert.TriggeredAt:yyyy-MM-dd HH:mm:ss} UTC"),
            $"Price: {Price(alert.StartPrice)} -> {Price(alert.EndPrice)}",
            string.Create(culture, $"Unusual by: {Math.Abs(alert.ZScore):0.0} standard deviations of the last hour"),
            string.Create(culture, $"Traded in the window: {alert.Volume:0.###}{BaseAssetSuffix(symbol)}"),
            string.Create(culture, $"Flagged seconds: {alert.FlaggedSeconds}"),
            $"Rule: {alert.RuleVersion}");

        return new Alert(title, body);
    }

    /// <summary>
    /// A price with six significant digits, so a cheap coin keeps what matters (<c>0.906</c>, not <c>0.91</c>) and an expensive one is not
    /// padded (<c>79893.4</c>). Trailing zeros are dropped.
    /// </summary>
    public static string Price(decimal price)
    {
        if (price == 0m)
            return "0";

        var decimals = Math.Clamp(5 - (int)Math.Floor(Math.Log10((double)Math.Abs(price))), 0, 8);
        return Math.Round(price, decimals).ToString("0.########", CultureInfo.InvariantCulture);
    }

    private static readonly string[] QuoteAssets = ["USDT", "USDC", "BUSD", "USD"];

    /// <summary>
    /// The volume is in the base asset (<c>SUI</c> for <c>SUIUSDT</c>), not in USDT: say so. The exchange's name carries no separator, so the
    /// quote asset is taken off the end; a name that does not end in a known one gets no unit rather than a wrong one.
    /// </summary>
    private static string BaseAssetSuffix(string symbol)
    {
        foreach (var quote in QuoteAssets)
            if (symbol.Length > quote.Length && symbol.EndsWith(quote, StringComparison.OrdinalIgnoreCase))
                return " " + symbol[..^quote.Length].ToUpperInvariant();

        return string.Empty;
    }

    /// <summary>The watch cannot see the market: no alert can come until it can.</summary>
    public static Alert Blind(string symbols, TimeSpan offlineFor) =>
        new("Watch is blind", string.Create(CultureInfo.InvariantCulture,
            $"No trades from the exchange for {offlineFor.TotalSeconds:0} s ({symbols}). Quiet now does not mean a calm market."));

    /// <summary>
    /// The symbol has no recent 1 minute candles to start from, so for a while the watch only listens. Without this message that time
    /// would look like a calm market.
    /// </summary>
    public static Alert WarmingUp(string symbol, TimeSpan silentFor) =>
        new($"Watch is warming up: {symbol}", string.Create(CultureInfo.InvariantCulture,
            $"There are no recent 1 minute candles of {symbol} to start from, so the watch cannot judge it for about {silentFor.TotalMinutes:0} minutes and will say nothing until then. Collect its 1 minute candles to start at once."));

    public static Alert Restored(string symbols, TimeSpan offlineFor) =>
        new("Watch sees the market again", string.Create(CultureInfo.InvariantCulture,
            $"The trade stream is back after {offlineFor.TotalSeconds:0} s ({symbols}). Moves during that time were not seen."));
}
