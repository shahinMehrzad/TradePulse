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

    /// <summary>A signal received earlier (taken or refused), or null.</summary>
    Task<ReceivedSignal?> FindSignalAsync(string signalId, CancellationToken cancellationToken);

    /// <summary>
    /// Records a received signal and, when it was taken, the trade it opened, in one step: either both are stored or neither, so a signal that
    /// was taken always has its trade. False when a signal with the same id was recorded before; nothing is changed then.
    /// </summary>
    Task<bool> RecordAsync(ReceivedSignal signal, PaperTrade? trade, CancellationToken cancellationToken);

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

    /// <summary>
    /// Saves how far each open trade has been followed (<see cref="PaperTrade.LastCheckedAt"/>) and its best and worst price so far, so a restart
    /// only has to look again at what came after. Trades that ended meanwhile are left as they are.
    /// </summary>
    Task SaveProgressAsync(IReadOnlyCollection<PaperTrade> openTrades, CancellationToken cancellationToken);

    /// <summary>
    /// Stores the result of an ended trade (<see cref="PaperTrade.Settle"/>), but only if it has none yet. False when somebody stored it first.
    /// </summary>
    Task<bool> SaveResultAsync(PaperTrade trade, CancellationToken cancellationToken);

    /// <summary>The trades that ended and have no result yet, oldest first: found again after a restart, or after the exchange did not answer.</summary>
    Task<IReadOnlyList<PaperTrade>> GetUnsettledAsync(CancellationToken cancellationToken);

    /// <summary>The trades that are open and have no message yet (the first send failed, or the service stopped before it), oldest first.</summary>
    Task<IReadOnlyList<PaperTrade>> GetOpenWithoutMessageAsync(CancellationToken cancellationToken);

    /// <summary>The trades that ended and whose end has not been told, oldest end first: a restart, or a chat that did not answer, leaves them here.</summary>
    Task<IReadOnlyList<PaperTrade>> GetEndsToAnnounceAsync(CancellationToken cancellationToken);

    /// <summary>Marks the end of a trade as told. False when it was marked already or the trade has not ended.</summary>
    Task<bool> MarkEndAnnouncedAsync(int tradeId, DateTime at, CancellationToken cancellationToken);

    /// <summary>Remembers the message of a trade. False when it already has one.</summary>
    Task<bool> AttachMessageAsync(int tradeId, long chatId, int messageId, CancellationToken cancellationToken);

    Task AddEventAsync(SignalEvent signalEvent, CancellationToken cancellationToken);
}
