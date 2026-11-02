using Application.Exchange;
using Application.Notifications;
using Application.Research.Watch;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.UnitTests;

internal sealed class FakeLiveTradeStream : ILiveTradeStream
{
    /// <summary>What the next subscriptions answer, in order; after the list is used up every call succeeds.</summary>
    public Queue<ExchangeResult<IAsyncDisposable>> Answers { get; } = [];

    public int SubscribeCalls { get; private set; }
    public ILiveTradeListener? Listener { get; private set; }
    public bool Unsubscribed { get; private set; }

    public Task<ExchangeResult<IAsyncDisposable>> SubscribeAsync(
        ExchangeEnum exchange, MarketTypeEnum marketType, IReadOnlyList<ExchangeSymbol> symbols, ILiveTradeListener listener, CancellationToken cancellationToken)
    {
        SubscribeCalls++;
        if (Answers.Count > 0)
            return Task.FromResult(Answers.Dequeue());

        Listener = listener;
        return Task.FromResult(ExchangeResult<IAsyncDisposable>.Ok(new Handle(this)));
    }

    private sealed class Handle(FakeLiveTradeStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            stream.Unsubscribed = true;
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakeShockAlertStore : IShockAlertStore
{
    private readonly List<ShockAlert> _saved = [];

    public bool Throws { get; set; }

    public IReadOnlyList<ShockAlert> Saved
    {
        get { lock (_saved) return _saved.ToList(); }
    }

    public Task SaveAsync(ShockAlert alert, CancellationToken cancellationToken)
    {
        if (Throws)
            throw new InvalidOperationException("the database is down");

        lock (_saved) _saved.Add(alert);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingNotifier(string name = "recording") : INotifier
{
    private readonly List<Alert> _sent = [];

    public string Name { get; } = name;
    public bool Throws { get; set; }

    public IReadOnlyList<Alert> Sent
    {
        get { lock (_sent) return _sent.ToList(); }
    }

    public Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        if (Throws)
            throw new NotificationException("could not reach the server");

        lock (_sent) _sent.Add(alert);
        return Task.CompletedTask;
    }
}
