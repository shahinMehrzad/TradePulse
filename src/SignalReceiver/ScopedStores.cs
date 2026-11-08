using Application.Signals;
using Domain.Entities;
using Domain.Enums;
using Infrastructure.Repositories;

namespace SignalReceiver;

/// <summary>
/// The signals' store for the long-lived parts of this program (the price watch, the chat, the webhook): every call has a database context of
/// its own that lives as long as the call. A context is not made for being shared by tasks or for living for days, and these parts do both.
/// </summary>
internal sealed class ScopedSignalStore(IServiceScopeFactory scopes) : ISignalStore
{
    private async Task<T> RunAsync<T>(Func<SignalStore, Task<T>> call)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<SignalStore>());
    }

    private async Task RunAsync(Func<SignalStore, Task> call)
    {
        await using var scope = scopes.CreateAsyncScope();
        await call(scope.ServiceProvider.GetRequiredService<SignalStore>());
    }

    public Task<bool> RecordAsync(ReceivedSignal signal, CancellationToken cancellationToken) => RunAsync(s => s.RecordAsync(signal, cancellationToken));
    public Task<bool> RecordAsync(ReceivedSignal signal, PaperTrade? trade, CancellationToken cancellationToken) => RunAsync(s => s.RecordAsync(signal, trade, cancellationToken));
    public Task<ReceivedSignal?> FindSignalAsync(string signalId, CancellationToken cancellationToken) => RunAsync(s => s.FindSignalAsync(signalId, cancellationToken));
    public Task<int> CountOpenAsync(ExchangeEnum exchange, string strategy, string symbol, CancellationToken cancellationToken) => RunAsync(s => s.CountOpenAsync(exchange, strategy, symbol, cancellationToken));
    public Task AddTradeAsync(PaperTrade trade, CancellationToken cancellationToken) => RunAsync(s => s.AddTradeAsync(trade, cancellationToken));
    public Task<IReadOnlyList<PaperTrade>> GetOpenTradesAsync(CancellationToken cancellationToken) => RunAsync(s => s.GetOpenTradesAsync(cancellationToken));
    public Task<bool> CloseAsync(PaperTrade trade, CancellationToken cancellationToken) => RunAsync(s => s.CloseAsync(trade, cancellationToken));
    public Task<bool> SaveResultAsync(PaperTrade trade, CancellationToken cancellationToken) => RunAsync(s => s.SaveResultAsync(trade, cancellationToken));
    public Task<IReadOnlyList<PaperTrade>> GetUnsettledAsync(CancellationToken cancellationToken) => RunAsync(s => s.GetUnsettledAsync(cancellationToken));
    public Task<IReadOnlyList<PaperTrade>> GetOpenWithoutMessageAsync(CancellationToken cancellationToken) => RunAsync(s => s.GetOpenWithoutMessageAsync(cancellationToken));
    public Task<IReadOnlyList<PaperTrade>> GetEndsToAnnounceAsync(CancellationToken cancellationToken) => RunAsync(s => s.GetEndsToAnnounceAsync(cancellationToken));
    public Task<bool> MarkEndAnnouncedAsync(int tradeId, DateTime at, CancellationToken cancellationToken) => RunAsync(s => s.MarkEndAnnouncedAsync(tradeId, at, cancellationToken));
    public Task SaveProgressAsync(IReadOnlyCollection<PaperTrade> openTrades, CancellationToken cancellationToken) => RunAsync(s => s.SaveProgressAsync(openTrades, cancellationToken));
    public Task<bool> AttachMessageAsync(int tradeId, long chatId, int messageId, CancellationToken cancellationToken) => RunAsync(s => s.AttachMessageAsync(tradeId, chatId, messageId, cancellationToken));
    public Task AddEventAsync(SignalEvent signalEvent, CancellationToken cancellationToken) => RunAsync(s => s.AddEventAsync(signalEvent, cancellationToken));
}

/// <summary>The listed symbols for the long-lived parts: read with a database context that lives as long as the read.</summary>
internal sealed class ScopedFollowedSymbols(IServiceScopeFactory scopes) : IFollowedSymbolSource
{
    public async Task<IReadOnlyList<FollowedSymbol>> GetAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ListedSymbols>().GetAsync(cancellationToken);
    }
}
