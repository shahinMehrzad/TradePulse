using Domain.Entities;

namespace Application.Signals;

/// <summary>
/// Where the signals, the paper trades opened from them and the log of both are kept. Every method is one statement that stays true when
/// two callers meet: a signal is recorded once, a trade ends once, a message is attached once.
/// </summary>
public interface ISignalStore
{
    /// <summary>Records a received signal (taken or refused). False when a signal with the same id was recorded before; nothing is changed then.</summary>
    Task<bool> RecordAsync(ReceivedSignal signal, CancellationToken cancellationToken);

    /// <summary>How many trades of <paramref name="strategy"/> on <paramref name="symbol"/> of <paramref name="exchange"/> are open now.</summary>
    Task<int> CountOpenAsync(Domain.Enums.ExchangeEnum exchange, string strategy, string symbol, CancellationToken cancellationToken);

    /// <summary>Stores a trade that has just opened. Its <see cref="PaperTrade.Id"/> is set.</summary>
    Task AddTradeAsync(PaperTrade trade, CancellationToken cancellationToken);

    /// <summary>Every open trade, oldest first: what the price watch follows, and what it loads again after a restart.</summary>
    Task<IReadOnlyList<PaperTrade>> GetOpenTradesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Stores how the trade ended (status, exit, best and worst price, result), but only if it is still open in the database.
    /// False when another caller ended it first: the trade is not touched and nothing else must be done for it (no second message).
    /// </summary>
    Task<bool> CloseAsync(PaperTrade trade, CancellationToken cancellationToken);

    /// <summary>Remembers the message of a trade. False when it already has one.</summary>
    Task<bool> AttachMessageAsync(int tradeId, long chatId, int messageId, CancellationToken cancellationToken);

    Task AddEventAsync(SignalEvent signalEvent, CancellationToken cancellationToken);
}
