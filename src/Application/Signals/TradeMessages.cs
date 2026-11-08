using System.Globalization;
using System.Net;
using Domain.Entities;
using Domain.Enums;

namespace Application.Signals;

/// <summary>
/// The words of a paper trade's message. Everything the project writes for people is English. Prices have
/// thousands separators and at least one decimal, percentages with two, a real minus sign. Costs are shown as what they did to the
/// result (a cost is negative; funding that was received is positive).
/// </summary>
public static class TradeMessages
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>The message when the trade opens.</summary>
    public static string Opened(PaperTrade trade) => Lines(
        $"🟢 <b>Paper trade opened · {Text(trade.Symbol)}</b>",
        $"Side: <b>{trade.Side}</b>",
            $"Max leverage: ×{Leverage(trade)} (the highest allowed, not a recommendation)",
        $"Entry: {Price(trade.EntryPrice)}",
        $"Target: {Price(trade.TakeProfit)} ({Percent(Move(trade.TakeProfit, trade.EntryPrice))})",
        $"Stop: {Price(trade.StopLoss)} ({Percent(Move(trade.StopLoss, trade.EntryPrice))})",
        $"Strategy: {Text(trade.Strategy)}",
        $"Risk to reward: 1 : {trade.RiskReward.ToString("0.0", Invariant)}",
        $"Status: ⏳ open since {Time(trade.OpenedAt, withDate: false)}");

    /// <summary>The same message once the trade has ended and its result is known: the new text of the message that was sent when it opened.</summary>
    public static string Ended(PaperTrade trade)
    {
        var (exit, closedAt, gross, fees, funding, net, r) = Result(trade);
        var cost = -(fees + funding);

        return Lines(
            $"{Icon(trade.Status)} <b>{Outcome(trade.Status)} · {Text(trade.Symbol)}</b>",
            $"Side: <b>{trade.Side}</b>",
            $"Max leverage: ×{Leverage(trade)} (the highest allowed, not a recommendation)",
            $"Entry: {Price(trade.EntryPrice)}",
            $"Exit: {Price(exit)}",
            $"Closed: {Time(closedAt, withDate: true)}",
            $"Duration: {Duration(closedAt - trade.OpenedAt)}",
            "────────",
            $"Result at the max leverage (×{Leverage(trade)}): {Percent(gross)}",
            $"Exchange fees (entry and exit): {Percent(-fees)}",
            $"Funding: {Percent(-funding)}",
            $"Exchange cost (fees + funding): {Percent(cost)}",
            $"<b>Net result: {Percent(net)}</b>",
            $"Worst drawdown on the way: {Percent(Adverse(trade))}",
            $"R: {Signed(r, "0.0")}",
            $"Strategy: {Text(trade.Strategy)}");
    }

    /// <summary>The short answer under the message, which is what makes the chat notify.</summary>
    public static string EndedReply(PaperTrade trade)
    {
        var (_, _, _, _, _, net, _) = Result(trade);
        return $"{Icon(trade.Status)} {Text(trade.Symbol)} {trade.Side} {Outcome(trade.Status).ToLowerInvariant()} · net {Percent(net)}";
    }

    private static (decimal Exit, DateTime ClosedAt, decimal Gross, decimal Fees, decimal Funding, decimal Net, decimal R) Result(PaperTrade trade) =>
        trade is { ExitPrice: { } exit, ClosedAt: { } closedAt, GrossResult: { } gross, FeesPaid: { } fees, FundingPaid: { } funding, NetResult: { } net, RMultiple: { } r }
            ? (exit, closedAt, gross, fees, funding, net, r)
            : throw new InvalidOperationException("The message of an ended trade needs its result.");

    private static string Lines(params string[] lines) => string.Join('\n', lines);

    private static string Text(string value) => WebUtility.HtmlEncode(value);

    private static string Leverage(PaperTrade trade) => trade.Leverage.ToString("0.##", Invariant);

    private static string Icon(PaperTradeStatusEnum status) => status switch
    {
        PaperTradeStatusEnum.TakeProfit => "✅",
        PaperTradeStatusEnum.StopLoss => "🛑",
        PaperTradeStatusEnum.Expired => "⌛",
        _ => "🟢",
    };

    private static string Outcome(PaperTradeStatusEnum status) => status switch
    {
        PaperTradeStatusEnum.TakeProfit => "Target hit",
        PaperTradeStatusEnum.StopLoss => "Stop hit",
        PaperTradeStatusEnum.Expired => "Expired",
        _ => "Open",
    };

    private static string Price(decimal price) => price.ToString("#,0.0#######", Invariant);

    /// <summary>How far <paramref name="to"/> is from <paramref name="from"/>, as a fraction of <paramref name="from"/>.</summary>
    private static decimal Move(decimal to, decimal from) => (to - from) / from;

    private static string Percent(decimal fraction) => Signed(fraction * 100, "0.00") + "%";

    private static string Signed(decimal value, string format)
    {
        var text = Math.Abs(value).ToString(format, Invariant);

        // A value that rounds to nothing has no sign.
        if (decimal.Parse(text, Invariant) == 0)
            return text;

        return (value < 0 ? "−" : "+") + text;
    }

    /// <summary>The worst the price went against the trade on the way, as a fraction of the entry (never positive).</summary>
    private static decimal Adverse(PaperTrade trade) =>
        trade.Side == PositionSideEnum.Long
            ? Math.Min(0, Move(trade.WorstPrice, trade.EntryPrice))
            : Math.Min(0, -Move(trade.WorstPrice, trade.EntryPrice));

    private static string Time(DateTime time, bool withDate) =>
        time.ToString(withDate ? "yyyy-MM-dd HH:mm" : "HH:mm", Invariant) + " UTC";

    private static string Duration(TimeSpan span)
    {
        var parts = new List<string>(3);
        if (span.Days > 0)
            parts.Add($"{span.Days}d");
        if (span.Days > 0 || span.Hours > 0)
            parts.Add($"{span.Hours}h");
        parts.Add($"{span.Minutes}m");
        return string.Join(' ', parts);
    }
}
