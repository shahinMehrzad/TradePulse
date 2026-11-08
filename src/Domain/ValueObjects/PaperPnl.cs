using Domain.Entities;
using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// What a closed paper trade earned, every part as a fraction of the margin (0.10 is +10 %), so leverage is already in.
/// <para>
/// <see cref="Fees"/> and <see cref="Funding"/> are costs: positive is paid. Funding can be negative (received): a long pays when the rate is
/// positive and receives when it is negative, a short the other way round. <see cref="ExchangeCost"/> is the two together, the figure the
/// message shows next to the net result.
/// </para>
/// </summary>
public readonly record struct PaperPnl(decimal Gross, decimal Fees, decimal Funding, decimal RMultiple)
{
    /// <summary>Binance's USDT-M taker fee, 0.04 %, paid on entry and on exit. A trade that ends on a stop or a target is a market order.</summary>
    public const decimal DefaultTakerFee = 0.0004m;

    public decimal ExchangeCost => Fees + Funding;

    public decimal Net => Gross - ExchangeCost;

    /// <param name="fundingHistory">
    /// Real settlements from the collector. One counts when the position was open at that moment: from the entry (included) to the exit
    /// (not included). A settlement without a mark price is taken at the entry price.
    /// </param>
    /// <param name="takerFee">One side's fee as a fraction of the traded value.</param>
    public static PaperPnl Calculate(PaperTrade trade, IEnumerable<FundingRate> fundingHistory, decimal takerFee = DefaultTakerFee)
    {
        if (trade is not { ExitPrice: { } exit, ClosedAt: { } closedAt })
            throw new InvalidOperationException("Only a closed trade has a result.");

        var direction = trade.Side == PositionSideEnum.Long ? 1m : -1m;
        var entry = trade.EntryPrice;
        var leverage = trade.Leverage;

        var gross = direction * (exit - entry) / entry * leverage;

        // Entry fee on the value at entry, exit fee on the value at exit.
        var fees = takerFee * leverage * (1 + exit / entry);

        var funding = fundingHistory
            .Where(f => f.FundingTime >= trade.OpenedAt && f.FundingTime < closedAt)
            .Sum(f => direction * f.Rate * leverage * ((f.MarkPrice > 0 ? f.MarkPrice : entry) / entry));

        var rMultiple = direction * (exit - entry) / Math.Abs(entry - trade.StopLoss);

        return new PaperPnl(gross, fees, funding, rMultiple);
    }
}
