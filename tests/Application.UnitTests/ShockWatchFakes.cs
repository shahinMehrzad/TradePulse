using Application.Exchange;
using Application.Notifications;
using Application.Research.Watch;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.ValueObjects;

namespace Application.UnitTests;

internal sealed class FakeLiveTradeStream : ILiveTradeStream
{
    private readonly Dictionary<string, ILiveTradeListener> _listeners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unsubscribed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What the next subscriptions answer, in order (whatever the symbol); after the list is used up every call succeeds.</summary>
    public Queue<ExchangeResult<IAsyncDisposable>> Answers { get; } = [];

    public int SubscribeCalls { get; private set; }

    /// <summary>The listener of the last symbol that was subscribed.</summary>
    public ILiveTradeListener? Listener { get; private set; }

    public ILiveTradeListener? ListenerOf(string symbol)
    {
        lock (_listeners) return _listeners.GetValueOrDefault(symbol);
    }

    /// <summary>True when any subscription was ended.</summary>
    public bool Unsubscribed
    {
        get { lock (_unsubscribed) return _unsubscribed.Count > 0; }
    }

    public bool WasUnsubscribed(string symbol)
    {
        lock (_unsubscribed) return _unsubscribed.Contains(symbol);
    }

    public Task<ExchangeResult<IAsyncDisposable>> SubscribeAsync(
        ExchangeEnum exchange, MarketTypeEnum marketType, IReadOnlyList<ExchangeSymbol> symbols, ILiveTradeListener listener, CancellationToken cancellationToken)
    {
        SubscribeCalls++;
        if (Answers.Count > 0)
            return Task.FromResult(Answers.Dequeue());

        var symbol = Assert.Single(symbols).Value;
        lock (_listeners) _listeners[symbol] = listener;
        Listener = listener;
        return Task.FromResult(ExchangeResult<IAsyncDisposable>.Ok(new Handle(this, symbol)));
    }

    private sealed class Handle(FakeLiveTradeStream stream, string symbol) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            lock (stream._unsubscribed) stream._unsubscribed.Add(symbol);
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>The symbols enabled for trading, as the trading service would say.</summary>
internal sealed class FakeTradedSymbolSource(params string[] enabled) : ITradedSymbolSource
{
    public HashSet<string> Enabled { get; } = new(enabled, StringComparer.OrdinalIgnoreCase);

    /// <summary>The trading service cannot be read.</summary>
    public bool Throws { get; set; }

    public Task<IReadOnlySet<string>> GetEnabledAsync(CancellationToken cancellationToken)
    {
        if (Throws)
            throw new InvalidOperationException("the trading service is down");

        return Task.FromResult<IReadOnlySet<string>>(Enabled.ToHashSet(StringComparer.OrdinalIgnoreCase));
    }
}

internal sealed class FakeSymbolCatalog(params SymbolListing[] listings) : Domain.Repositories.ISymbolCatalogRepository
{
    public Task<IReadOnlyList<SymbolListing>> GetListings(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SymbolListing>>(listings);
    public Task<SymbolListing?> GetListing(int id, CancellationToken cancellationToken) => Task.FromResult(listings.FirstOrDefault(l => l.Id == id));
    public Task<SymbolLookup?> GetSymbol(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<(IReadOnlyList<SymbolLookup> Items, int Total)> GetSymbolsPage(string? keyword, int skip, int take, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyDictionary<int, (int Series, int Settings, int Watches)>> GetListingUsage(int symbolId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<SymbolLookup> UpdateSymbolDescription(int id, string? description, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<int> AddSymbol(SymbolLookup symbol, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task RemoveSymbol(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<int> AddListing(SymbolListing listing, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task RemoveListing(int id, CancellationToken cancellationToken) => throw new NotSupportedException();
}

internal sealed class FakeWatchedSymbolSource : IWatchedSymbolSource
{
    public List<WatchedSymbol> Symbols { get; } = [];

    /// <summary>The list cannot be read (the database is down).</summary>
    public bool Throws { get; set; }

    public Task<IReadOnlyList<WatchedSymbol>> GetAsync(CancellationToken cancellationToken)
    {
        if (Throws)
            throw new InvalidOperationException("the database is down");

        lock (Symbols) return Task.FromResult<IReadOnlyList<WatchedSymbol>>(Symbols.ToList());
    }
}

internal sealed class FakeShockWatchConfigurationRepository : Application.Research.Watch.IShockWatchConfigurationRepository
{
    private int _nextId = 1;

    public List<ShockWatchConfiguration> Rows { get; } = [];

    public Task<IReadOnlyList<ShockWatchConfiguration>> GetAll(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ShockWatchConfiguration>>(Rows.OrderBy(r => r.SymbolListing?.ExchangeSymbol).ToList());

    public Task<ShockWatchConfiguration?> GetById(int id, CancellationToken cancellationToken) =>
        Task.FromResult(Rows.FirstOrDefault(r => r.Id == id));

    public Task<int> Add(ShockWatchConfiguration configuration, CancellationToken cancellationToken)
    {
        if (Rows.Any(r => r.SymbolListingId == configuration.SymbolListingId))
            throw new ConfigurationConflictException("This symbol is on the list already.");

        configuration.Id = _nextId++;
        Rows.Add(configuration);
        return Task.FromResult(configuration.Id);
    }

    public Task SetEnabled(int id, bool enabled, CancellationToken cancellationToken)
    {
        (Rows.FirstOrDefault(r => r.Id == id) ?? throw new ConfigurationNotFoundException("Configuration does not exist.")).IsEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task Decide(int id, WatchReasonEnum reason, bool enabled, CancellationToken cancellationToken)
    {
        var row = Rows.FirstOrDefault(r => r.Id == id) ?? throw new ConfigurationNotFoundException("Configuration does not exist.");
        row.Reason = reason;
        row.IsEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task Remove(int id, CancellationToken cancellationToken)
    {
        Rows.Remove(Rows.FirstOrDefault(r => r.Id == id) ?? throw new ConfigurationNotFoundException("Configuration does not exist."));
        return Task.CompletedTask;
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
