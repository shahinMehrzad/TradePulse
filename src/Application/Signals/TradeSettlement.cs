using Application.Exchange;
using Domain.Entities;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Signals;

/// <summary>What the exchange fees and the funding cost, and when to settle.</summary>
public sealed record SettlementOptions
{
    /// <summary>One side's fee as a fraction of the traded value (Binance USDT-M taker, 0.04 %).</summary>
    public decimal TakerFee { get; init; } = PaperPnl.DefaultTakerFee;

    /// <summary>
    /// A funding settlement is published by the exchange a little after its moment. A trade that ended in the first minutes after a full hour (every
    /// funding moment of Binance is on one) waits until this long after the hour, so a settlement it paid is never missing from its result.
    /// </summary>
    public TimeSpan FundingLag { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>What came of settling a trade.</summary>
public abstract record SettlementOutcome
{
    private SettlementOutcome() { }

    /// <summary>The result was worked out and kept on the trade (and stored, unless somebody stored it first).</summary>
    public sealed record Settled(PaperPnl Result) : SettlementOutcome;

    /// <summary>The trade had a result already.</summary>
    public sealed record AlreadySettled : SettlementOutcome;

    /// <summary>Too soon after a funding moment: ask again at <paramref name="Until"/>.</summary>
    public sealed record Postponed(DateTime Until) : SettlementOutcome;

    /// <summary>The exchange did not answer. Nothing was changed: ask again later.</summary>
    public sealed record Failed(string? Error) : SettlementOutcome;
}

/// <summary>
/// Works out what an ended paper trade earned: the move with its leverage, the fees of entering and leaving, and the funding it paid or
/// received while it was open. The funding is asked of the exchange for the trade's own stretch, whatever the collector keeps, so the
/// result is right for any symbol and does not depend on a table being filled.
/// </summary>
public sealed class TradeSettlement(
    IFundingRateGateway funding, ISignalStore store, IFollowedSymbolSource symbols, SettlementOptions options, TimeProvider time, ILogger<TradeSettlement> logger)
{
    public async Task<SettlementOutcome> SettleAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        if (trade.IsOpen)
            throw new InvalidOperationException("Only a trade that has ended can be settled.");

        if (trade.NetResult is not null)
            return new SettlementOutcome.AlreadySettled();

        var closedAt = trade.ClosedAt!.Value;
        if (WaitUntil(closedAt) is { } until && time.GetUtcNow().UtcDateTime < until)
            return new SettlementOutcome.Postponed(until);

        var exchangeSymbol = (await ExchangeSymbolAsync(trade, cancellationToken));
        var settlements = new List<FundingRate>();
        var from = trade.OpenedAt;

        while (true)
        {
            var answer = await funding.GetFundingRatesAsync(trade.Exchange, exchangeSymbol, from, cancellationToken);
            if (!answer.Success)
            {
                logger.LogWarning("The funding of the trade {Id} ({Symbol}) could not be read ({Error}). The trade is settled later", trade.Id, trade.Symbol, answer.Error);
                return new SettlementOutcome.Failed(answer.Error);
            }

            var page = answer.Value;
            settlements.AddRange(page.Items);

            if (!page.HasMore || page.Items.Count == 0 || page.Items.Max(f => f.FundingTime) >= closedAt)
                break;

            from = page.Items.Max(f => f.FundingTime).AddMilliseconds(1);
        }

        var result = trade.Settle(settlements, options.TakerFee);
        await store.SaveResultAsync(trade, cancellationToken);
        return new SettlementOutcome.Settled(result);
    }

    /// <summary>The moment from which the funding of a trade that ended at <paramref name="closedAt"/> is all published; null when there is nothing to wait for.</summary>
    private DateTime? WaitUntil(DateTime closedAt)
    {
        var hour = new DateTime(closedAt.Ticks - closedAt.Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc);
        var until = hour + options.FundingLag;
        return closedAt < until ? until : null;
    }

    private async Task<Domain.ValueObjects.ExchangeSymbol> ExchangeSymbolAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        try
        {
            var listed = (await symbols.GetAsync(cancellationToken)).FirstOrDefault(s => s.Exchange == trade.Exchange && s.Symbol == trade.Symbol);
            if (listed is not null)
                return listed.ExchangeSymbol;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The listings could not be read ({Error}); the name of {Symbol} on the exchange is taken from its own", ex.GetType().Name, trade.Symbol);
        }

        // No longer listed (or the list cannot be read): the signal's name without the slash.
        return FollowedSymbol.Guess(trade.Exchange, trade.Symbol).ExchangeSymbol;
    }
}
