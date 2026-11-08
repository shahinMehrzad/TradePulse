using Application.Signals;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace SignalReceiver.IntegrationTests;

/// <summary>The receiver as it starts, with its own database, exchange and chat replaced by memory: what is tested is the program's HTTP face.</summary>
public sealed class ReceiverFactory(string? secret = ReceiverFactory.Secret) : WebApplicationFactory<Program>
{
    public const string Secret = "a-secret-of-at-least-thirty-two-characters-long";

    /// <summary>The moment of the tests: two seconds after a 4h candle closed.</summary>
    public static readonly DateTime Now = new(2026, 11, 7, 8, 0, 2, DateTimeKind.Utc);

    public static readonly DateTime CandleOpen = new(2026, 11, 7, 4, 0, 0, DateTimeKind.Utc);

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(Now));
    public MemoryStore Store { get; } = new();
    public PriceBoard Board { get; } = new();
    public List<PaperTrade> Tracked { get; } = [];
    public List<PaperTrade> Told { get; } = [];
    public List<SignalStrategyConfiguration> Strategies { get; } =
    [
        new() { Id = 1, Strategy = "S1", IsEnabled = true, MaxSignalAgeSeconds = 3600, MaxMoveOfStopDistance = 0.25m, MaxOpenPerSymbol = null, MaxLeverage = 20m },
    ];

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // UseSetting, not the configuration callbacks: the program reads its settings while it is being built, before those callbacks run.
        builder.UseSetting("ConnectionStrings:DefaultConnection", "Host=localhost;Database=unused;Username=unused;Password=unused");
        if (secret is not null)
            builder.UseSetting("Signals:Webhook:Secret", secret);

        builder.ConfigureTestServices(services =>
        {
            // The price watch and the chat would talk to Binance and Telegram: not in these tests.
            foreach (var hosted in services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Name is "PaperTradeWatcherService" or "TradeAnnouncerService").ToList())
                services.Remove(hosted);

            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            services.RemoveAll<ISignalStore>();
            services.AddSingleton<ISignalStore>(Store);
            services.RemoveAll<ISignalStrategyConfigurationRepository>();
            services.AddSingleton<ISignalStrategyConfigurationRepository>(new MemoryStrategies(Strategies));
            services.RemoveAll<IFollowedSymbolSource>();
            services.AddSingleton<IFollowedSymbolSource>(new MemorySymbols());
            services.RemoveAll<PriceBoard>();
            services.AddSingleton(Board);
            services.RemoveAll<ITradeTracker>();
            services.AddSingleton<ITradeTracker>(new Handover(Tracked, Told));
            services.RemoveAll<IOpenedTradeSink>();
            services.AddSingleton<IOpenedTradeSink>(new Handover(Tracked, Told));
        });
    }

    private sealed class Handover(List<PaperTrade> tracked, List<PaperTrade> told) : ITradeTracker, IOpenedTradeSink
    {
        void ITradeTracker.Track(PaperTrade trade) { lock (tracked) tracked.Add(trade); }
        void IOpenedTradeSink.Opened(PaperTrade trade) { lock (told) told.Add(trade); }
    }

    private sealed class MemorySymbols : IFollowedSymbolSource
    {
        public Task<IReadOnlyList<FollowedSymbol>> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<FollowedSymbol>>([new FollowedSymbol(ExchangeEnum.Binance, MarketTypeEnum.Futures, "BTC/USDT", new ExchangeSymbol("BTCUSDT"))]);
    }

    private sealed class MemoryStrategies(List<SignalStrategyConfiguration> rows) : ISignalStrategyConfigurationRepository
    {
        public Task<SignalStrategyConfiguration?> GetByStrategy(string strategy, CancellationToken cancellationToken) => Task.FromResult(rows.FirstOrDefault(r => r.Strategy == strategy));
        public Task<IReadOnlyList<SignalStrategyConfiguration>> GetAll(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SignalStrategyConfiguration>>(rows.ToList());
        public Task<int> Add(SignalStrategyConfiguration configuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task Update(SignalStrategyConfiguration configuration, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SetEnabled(int id, bool enabled, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

/// <summary>Only what the webhook uses of the store.</summary>
public sealed class MemoryStore : ISignalStore
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ReceivedSignal> _signals = [];
    private readonly List<PaperTrade> _trades = [];

    public IReadOnlyDictionary<string, ReceivedSignal> Signals
    {
        get { lock (_lock) return _signals.ToDictionary(p => p.Key, p => p.Value); }
    }

    public IReadOnlyList<PaperTrade> Trades
    {
        get { lock (_lock) return _trades.ToList(); }
    }

    public Task<ReceivedSignal?> FindSignalAsync(string signalId, CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult(_signals.GetValueOrDefault(signalId));
    }

    public Task<bool> RecordAsync(ReceivedSignal signal, PaperTrade? trade, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_signals.TryAdd(signal.SignalId, signal))
                return Task.FromResult(false);

            if (trade is not null)
            {
                typeof(PaperTrade).GetProperty(nameof(PaperTrade.Id))!.SetValue(trade, 500 + _trades.Count);
                _trades.Add(trade);
            }

            return Task.FromResult(true);
        }
    }

    public Task<int> CountOpenAsync(ExchangeEnum exchange, string strategy, string symbol, CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult(_trades.Count(t => t.IsOpen && t.Exchange == exchange && t.Strategy == strategy && t.Symbol == symbol));
    }

    public Task AddEventAsync(SignalEvent signalEvent, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> RecordAsync(ReceivedSignal signal, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task AddTradeAsync(PaperTrade trade, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<PaperTrade>> GetOpenTradesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> CloseAsync(PaperTrade trade, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> SaveResultAsync(PaperTrade trade, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<PaperTrade>> GetUnsettledAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<PaperTrade>> GetOpenWithoutMessageAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<IReadOnlyList<PaperTrade>> GetEndsToAnnounceAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> MarkEndAnnouncedAsync(int tradeId, DateTime at, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task SaveProgressAsync(IReadOnlyCollection<PaperTrade> openTrades, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> AttachMessageAsync(int tradeId, long chatId, int messageId, CancellationToken cancellationToken) => throw new NotSupportedException();
}
