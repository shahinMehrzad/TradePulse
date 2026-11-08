using Application.Signals;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public class SignalStore(SignalsDbContext context) : ISignalStore
{
    public async Task<bool> RecordAsync(ReceivedSignal signal, CancellationToken cancellationToken)
    {
        context.Signals.Add(signal);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
        finally
        {
            // The service lives for days with this one context: nothing may pile up in its tracker, and a refused row must not be tried again.
            context.ChangeTracker.Clear();
        }
    }

    public Task<int> CountOpenAsync(ExchangeEnum exchange, string strategy, string symbol, CancellationToken cancellationToken) =>
        context.PaperTrades.CountAsync(
            t => t.Exchange == exchange && t.Strategy == strategy && t.Symbol == symbol && t.Status == PaperTradeStatusEnum.Open, cancellationToken);

    public async Task AddTradeAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        context.PaperTrades.Add(trade);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }

    public async Task<IReadOnlyList<PaperTrade>> GetOpenTradesAsync(CancellationToken cancellationToken) =>
        await context.PaperTrades.AsNoTracking()
            .Where(t => t.Status == PaperTradeStatusEnum.Open)
            .OrderBy(t => t.Id)
            .ToListAsync(cancellationToken);

    public async Task<bool> CloseAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        // The condition is the point: of two callers that both think they ended the trade, only one changes a row.
        var changed = await context.PaperTrades
            .Where(t => t.Id == trade.Id && t.Status == PaperTradeStatusEnum.Open)
            .ExecuteUpdateAsync(set => set
                .SetProperty(t => t.Status, trade.Status)
                .SetProperty(t => t.ExitPrice, trade.ExitPrice)
                .SetProperty(t => t.ClosedAt, trade.ClosedAt)
                .SetProperty(t => t.BestPrice, trade.BestPrice)
                .SetProperty(t => t.WorstPrice, trade.WorstPrice)
                .SetProperty(t => t.GrossResult, trade.GrossResult)
                .SetProperty(t => t.FeesPaid, trade.FeesPaid)
                .SetProperty(t => t.FundingPaid, trade.FundingPaid)
                .SetProperty(t => t.NetResult, trade.NetResult)
                .SetProperty(t => t.RMultiple, trade.RMultiple), cancellationToken);

        return changed == 1;
    }

    public async Task<bool> AttachMessageAsync(int tradeId, long chatId, int messageId, CancellationToken cancellationToken) =>
        await context.PaperTrades
            .Where(t => t.Id == tradeId && t.TelegramMessageId == null)
            .ExecuteUpdateAsync(set => set
                .SetProperty(t => t.TelegramChatId, chatId)
                .SetProperty(t => t.TelegramMessageId, messageId), cancellationToken) == 1;

    public async Task AddEventAsync(SignalEvent signalEvent, CancellationToken cancellationToken)
    {
        context.Events.Add(signalEvent);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            context.ChangeTracker.Clear();
        }
    }
}
