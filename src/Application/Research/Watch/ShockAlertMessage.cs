using System.Globalization;
using Application.Notifications;
using Domain.Entities;
using Domain.Enums;

namespace Application.Research.Watch;

/// <summary>
/// The words of the watch. Short, with the symbol in bold and an emoji for the direction, so the admin sees at a glance which symbol moved and
/// which way. Plain text for every channel, and the same words with Telegram's formatting. A shock alert says what the price did, never what
/// it will do and never what to do: after such a move the price goes on the same way only about as often as it turns back.
/// </summary>
public static class ShockAlertMessage
{
    private static readonly CultureInfo Culture = CultureInfo.InvariantCulture;

    public static Alert Create(ShockAlert alert, string symbol)
    {
        var up = alert.Direction == EventDirectionEnum.Up;
        var move = string.Create(Culture, $"{(up ? "+" : "-")}{Math.Abs(alert.Move) * 100:0.00} %");
        var window = Window(alert.WindowSeconds);
        var times = string.Create(Culture, $"{Math.Abs(alert.ZScore):0.#}x");
        var prices = $"{Price(alert.StartPrice)} -> {Price(alert.EndPrice)}";
        var at = string.Create(Culture, $"{alert.TriggeredAt:HH:mm} UTC");

        var title = $"{symbol} {move} in {window}";
        var body = $"{prices}, {times} its usual move, {at}";
        var html = $"{(up ? "🟢⬆️" : "🔴⬇️")} <b>{Escape(symbol)}</b> {move} in {window}\n{prices.Replace("->", "→")} · {times} usual · {at}";

        return new Alert(title, body, html);
    }

    /// <summary>The watch cannot see the market: no alert can come until it can.</summary>
    public static Alert Blind(string symbols, TimeSpan offlineFor) =>
        Message("⚠️", "Watch is blind", string.Create(Culture, $"{symbols}: no connection for {offlineFor.TotalSeconds:0} s. Quiet is not calm."));

    /// <summary>
    /// The symbol has no recent 1 minute candles to start from, so for a while the watch only listens. Without this message that time
    /// would look like a calm market.
    /// </summary>
    public static Alert WarmingUp(string symbol, TimeSpan silentFor) =>
        Message("⏳", $"{symbol} warming up", string.Create(Culture, $"No recent 1 minute candles: silent for about {silentFor.TotalMinutes:0} min. Collect them to start at once."), subject: symbol);

    public static Alert Restored(string symbols, TimeSpan offlineFor) =>
        Message("✅", "Watch sees the market again", string.Create(Culture, $"{symbols}: back after {offlineFor.TotalSeconds:0} s. Moves meanwhile were not seen."));

    /// <summary>One message when the watch starts, so the admin sees restarts too.</summary>
    public static Alert Started(string following, ShockWatchOptions rule) =>
        Message("👀", "Watch started", string.Create(Culture,
            $"{following}\nSudden: {rule.WindowSeconds} s at {rule.MinZScore}x · Gradual: {rule.GradualWindowMinutes} min at {rule.GradualMinZScore}x\nRules {rule.SuddenVersion}, {rule.GradualVersion}"));

    /// <summary>
    /// A message about the watch itself: an emoji and a bold title, then the detail. <paramref name="subject"/> is a symbol inside the title that
    /// should stand out (the title is then the whole bold part).
    /// </summary>
    private static Alert Message(string emoji, string title, string detail, string? subject = null)
    {
        var html = subject is null
            ? $"{emoji} <b>{Escape(title)}</b>\n{Escape(detail)}"
            : $"{emoji} <b>{Escape(subject)}</b>{Escape(title[subject.Length..])}\n{Escape(detail)}";

        return new Alert(title, detail, html);
    }

    /// <summary>The window as the admin thinks of it: seconds below two minutes, minutes above.</summary>
    public static string Window(int seconds) => seconds < 120 ? $"{seconds} s" : $"{seconds / 60} min";

    /// <summary>
    /// A price with six significant digits, so a cheap coin keeps what matters (<c>0.906</c>, not <c>0.91</c>) and an expensive one is not
    /// padded (<c>79893.4</c>). Trailing zeros are dropped.
    /// </summary>
    public static string Price(decimal price)
    {
        if (price == 0m)
            return "0";

        var decimals = Math.Clamp(5 - (int)Math.Floor(Math.Log10((double)Math.Abs(price))), 0, 8);
        return Math.Round(price, decimals).ToString("0.########", Culture);
    }

    // Telegram's HTML needs only these three escaped.
    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
