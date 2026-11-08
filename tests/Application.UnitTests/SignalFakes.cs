using Application.Exchange;
using Application.Notifications;
using Application.Signals;
using Domain.Entities;
using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;

namespace Application.UnitTests;

/// <summary>The signals' tables, in memory. Open trades are the very objects the watcher holds, so a test can look at their state.</summary>
internal sealed class FakeSignalStore : ISignalStore
{
    private readonly object _lock = new();
    private readonly List<PaperTrade> _closed = [];
    private int _saveCalls;

    public List<PaperTrade> Open { get; } = [];

    /// <summary>The next calls to end a trade fail as if the database were busy.</summary>
    public int CloseFailures { get; set; }

    /// <summary>Another caller ended the trade first: the row does not change.</summary>
    public bool EndedElsewhere { get; set; }

    public IReadOnlyList<PaperTrade> Closed
    {
        get { lock (_lock) return _closed.ToList(); }
    }

    public int SaveCalls => Volatile.Read(ref _saveCalls);

    public Task<IReadOnlyList<PaperTrade>> GetOpenTradesAsync(CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<PaperTrade>>(Open.ToList());
    }

    public Task<bool> CloseAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (CloseFailures > 0)
            {
                CloseFailures--;
                throw new InvalidOperationException("the database is busy");
            }

            if (EndedElsewhere)
                return Task.FromResult(false);

            _closed.Add(trade);
            return Task.FromResult(true);
        }
    }

    public Task SaveProgressAsync(IReadOnlyCollection<PaperTrade> openTrades, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _saveCalls);
        return Task.CompletedTask;
    }

    private readonly List<PaperTrade> _settled = [];

    /// <summary>The trades whose result was stored.</summary>
    public IReadOnlyList<PaperTrade> Settled
    {
        get { lock (_lock) return _settled.ToList(); }
    }

    public Task<bool> SaveResultAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_settled.Contains(trade))
                return Task.FromResult(false);

            _settled.Add(trade);
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<PaperTrade>> GetUnsettledAsync(CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<PaperTrade>>(_closed.Where(t => !_settled.Contains(t)).ToList());
    }

    private readonly List<SignalEvent> _events = [];
    private readonly Dictionary<int, (long ChatId, int MessageId)> _attached = [];
    private readonly Dictionary<int, DateTime> _endsTold = [];

    /// <summary>The log lines, in order.</summary>
    public IReadOnlyList<SignalEvent> Events
    {
        get { lock (_lock) return _events.ToList(); }
    }

    public IReadOnlyDictionary<int, (long ChatId, int MessageId)> Attached
    {
        get { lock (_lock) return _attached.ToDictionary(p => p.Key, p => p.Value); }
    }

    public IReadOnlyDictionary<int, DateTime> EndsTold
    {
        get { lock (_lock) return _endsTold.ToDictionary(p => p.Key, p => p.Value); }
    }

    /// <summary>The next calls to write a log line fail (the database is busy).</summary>
    public bool EventsFail { get; set; }

    public Task<IReadOnlyList<PaperTrade>> GetOpenWithoutMessageAsync(CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<PaperTrade>>(Open.Where(t => t.IsOpen && !_attached.ContainsKey(t.Id) && t.TelegramMessageId is null).ToList());
    }

    public Task<IReadOnlyList<PaperTrade>> GetEndsToAnnounceAsync(CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult<IReadOnlyList<PaperTrade>>(_closed.Where(t => !_endsTold.ContainsKey(t.Id)).ToList());
    }

    public Task<bool> MarkEndAnnouncedAsync(int tradeId, DateTime at, CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult(_endsTold.TryAdd(tradeId, at));
    }

    public Task<bool> AttachMessageAsync(int tradeId, long chatId, int messageId, CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult(_attached.TryAdd(tradeId, (chatId, messageId)));
    }

    public Task AddEventAsync(SignalEvent signalEvent, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (EventsFail)
                throw new InvalidOperationException("the database is busy");

            _events.Add(signalEvent);
            return Task.CompletedTask;
        }
    }

    public Task<bool> RecordAsync(ReceivedSignal signal, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<int> CountOpenAsync(ExchangeEnum exchange, string strategy, string symbol, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task AddTradeAsync(PaperTrade trade, CancellationToken cancellationToken) => throw new NotSupportedException();
}

/// <summary>The chat of the paper trades, in memory.</summary>
internal sealed class FakeTradeChat : ITradeChat
{
    private readonly object _lock = new();
    private readonly List<(ChatMessage Message, string Text)> _sent = [];
    private readonly List<(ChatMessage Message, string Text)> _edits = [];
    private readonly List<(ChatMessage To, string Text)> _replies = [];
    private int _nextId = 100;

    /// <summary>The next sends fail as if the chat were unreachable.</summary>
    public int SendFailures { get; set; }

    /// <summary>The next call is refused as "too many requests" with this wait; the one after it works.</summary>
    public TimeSpan? RetryAfterOnce { get; set; }

    /// <summary>The message is gone from the chat (deleted): an edit says so.</summary>
    public bool MessageGone { get; set; }

    public IReadOnlyList<(ChatMessage Message, string Text)> Sent
    {
        get { lock (_lock) return _sent.ToList(); }
    }

    public IReadOnlyList<(ChatMessage Message, string Text)> Edits
    {
        get { lock (_lock) return _edits.ToList(); }
    }

    public IReadOnlyList<(ChatMessage To, string Text)> Replies
    {
        get { lock (_lock) return _replies.ToList(); }
    }

    public int Attempts { get; private set; }

    public Task<ChatMessage> SendAsync(string html, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Attempts++;
            Refuse();
            if (SendFailures > 0)
            {
                SendFailures--;
                throw new NotificationException("Telegram could not be reached (ConnectionError)");
            }

            var message = new ChatMessage(-1001, _nextId++);
            _sent.Add((message, html));
            return Task.FromResult(message);
        }
    }

    public Task<ChatMessage> ReplyAsync(ChatMessage to, string html, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Attempts++;
            Refuse();
            _replies.Add((to, html));
            return Task.FromResult(new ChatMessage(to.ChatId, _nextId++));
        }
    }

    public Task<bool> EditAsync(ChatMessage message, string html, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Attempts++;
            Refuse();
            if (MessageGone)
                return Task.FromResult(false);

            _edits.Add((message, html));
            return Task.FromResult(true);
        }
    }

    private void Refuse()
    {
        if (RetryAfterOnce is not { } wait)
            return;

        RetryAfterOnce = null;
        throw new NotificationException("Telegram answered 429: Too Many Requests", wait);
    }
}

/// <summary>The exchange's 1 minute candles, scripted.</summary>
internal sealed class FakeCandleGateway : ICandleGateway
{
    private readonly object _lock = new();
    private readonly List<(DateTime From, DateTime? To)> _calls = [];
    private readonly List<Candle> _candles = [];

    public bool Fail { get; set; }

    /// <summary>Answers with every candle from the start, whatever the end asked for (to test that the minute in progress is never used).</summary>
    public bool IgnoreTo { get; set; }

    public int PageSize { get; set; } = 1_000;

    public void Add(params Candle[] candles)
    {
        lock (_lock) _candles.AddRange(candles);
    }

    public IReadOnlyList<(DateTime From, DateTime? To)> Calls
    {
        get { lock (_lock) return _calls.ToList(); }
    }

    public Task<ExchangeResult<ExchangePage<Candle>>> GetCandlesAsync(CandleSeries series, DateTime from, DateTime? to, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls.Add((from, to));
            if (Fail)
                return Task.FromResult(ExchangeResult<ExchangePage<Candle>>.Fail("the exchange is down"));

            var wanted = _candles
                .Where(c => c.TimeRange.OpenTime >= from && (IgnoreTo || to is null || c.TimeRange.OpenTime <= to))
                .OrderBy(c => c.TimeRange.OpenTime)
                .ToList();
            var page = wanted.Take(PageSize).ToList();
            return Task.FromResult(ExchangeResult<ExchangePage<Candle>>.Ok(new ExchangePage<Candle>(page, wanted.Count > page.Count)));
        }
    }
}

/// <summary>The trades of single minutes, scripted.</summary>
internal sealed class FakeHistoricalTrades : IHistoricalTradeGateway
{
    private readonly object _lock = new();
    private readonly Dictionary<DateTime, List<LiveTrade>> _minutes = [];
    private int _calls;

    public bool Fail { get; set; }

    public int Calls => Volatile.Read(ref _calls);

    public void Add(DateTime minute, params LiveTrade[] trades)
    {
        lock (_lock) _minutes[minute] = [.. trades];
    }

    public Task<ExchangeResult<IReadOnlyList<LiveTrade>>> GetTradesAsync(
        ExchangeEnum exchange, MarketTypeEnum marketType, ExchangeSymbol symbol, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);
        lock (_lock)
        {
            if (Fail)
                return Task.FromResult(ExchangeResult<IReadOnlyList<LiveTrade>>.Fail("the exchange is down"));

            var trades = _minutes.Where(m => m.Key >= from && m.Key < to).SelectMany(m => m.Value).ToList();
            return Task.FromResult(ExchangeResult<IReadOnlyList<LiveTrade>>.Ok(trades));
        }
    }
}

/// <summary>The funding settlements of an exchange, scripted.</summary>
internal sealed class FakeFundingGateway : IFundingRateGateway
{
    private readonly object _lock = new();
    private readonly List<FundingRate> _rates = [];
    private readonly List<(ExchangeEnum Exchange, string Symbol, DateTime From)> _calls = [];

    public bool Fail { get; set; }

    public int PageSize { get; set; } = 1_000;

    public void Add(params FundingRate[] rates)
    {
        lock (_lock) _rates.AddRange(rates);
    }

    public IReadOnlyList<(ExchangeEnum Exchange, string Symbol, DateTime From)> Calls
    {
        get { lock (_lock) return _calls.ToList(); }
    }

    public Task<ExchangeResult<ExchangePage<FundingRate>>> GetFundingRatesAsync(
        ExchangeEnum exchange, ExchangeSymbol symbol, DateTime from, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls.Add((exchange, symbol.Value, from));
            if (Fail)
                return Task.FromResult(ExchangeResult<ExchangePage<FundingRate>>.Fail("the exchange is down"));

            var wanted = _rates.Where(r => r.FundingTime >= from).OrderBy(r => r.FundingTime).ToList();
            var page = wanted.Take(PageSize).ToList();
            return Task.FromResult(ExchangeResult<ExchangePage<FundingRate>>.Ok(new ExchangePage<FundingRate>(page, wanted.Count > page.Count)));
        }
    }
}

internal sealed class FakeFollowedSymbols(params FollowedSymbol[] symbols) : IFollowedSymbolSource
{
    public List<FollowedSymbol> Symbols { get; } = [.. symbols];

    public bool Throws { get; set; }

    public Task<IReadOnlyList<FollowedSymbol>> GetAsync(CancellationToken cancellationToken)
    {
        if (Throws)
            throw new InvalidOperationException("the database is down");

        lock (Symbols) return Task.FromResult<IReadOnlyList<FollowedSymbol>>(Symbols.ToList());
    }
}

internal sealed class RecordingTradeEvents : ITradeEvents
{
    private readonly List<PaperTrade> _closed = [];

    public bool Throws { get; set; }

    public IReadOnlyList<PaperTrade> Closed
    {
        get { lock (_closed) return _closed.ToList(); }
    }

    public Task OnClosedAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        lock (_closed) _closed.Add(trade);
        if (Throws)
            throw new InvalidOperationException("the message could not be sent");

        return Task.CompletedTask;
    }
}

/// <summary>One 4h candle's signal and the paper trades of the watch tests.</summary>
internal static class SignalTestData
{
    public static readonly DateTime CandleOpen = new(2026, 11, 7, 4, 0, 0, DateTimeKind.Utc);

    /// <summary>When the trade opens: two seconds after the 4h candle closed, ten past eight.</summary>
    public static readonly DateTime Opened = CandleOpen.AddHours(4).AddSeconds(2);

    /// <summary>The minute the trade opens in (08:00).</summary>
    public static readonly DateTime FirstMinute = CandleOpen.AddHours(4);

    public static readonly FollowedSymbol Btc = new(ExchangeEnum.Binance, MarketTypeEnum.Futures, "BTC/USDT", new ExchangeSymbol("BTCUSDT"));
    public static readonly FollowedSymbol Eth = new(ExchangeEnum.Binance, MarketTypeEnum.Futures, "ETH/USDT", new ExchangeSymbol("ETHUSDT"));

    /// <summary>A long at 100: target 104, stop 98.</summary>
    public static PaperTrade Long(DateTime? openedAt = null, string symbol = "BTC/USDT", int id = 1, DateTime? candleOpen = null, TimeSpan? maxDuration = null) => Open(PositionSideEnum.Long, 98m, 104m, openedAt, symbol, id, candleOpen, maxDuration);

    /// <summary>A short at 100: target 96, stop 102.</summary>
    public static PaperTrade Short(DateTime? openedAt = null, string symbol = "BTC/USDT", int id = 1) => Open(PositionSideEnum.Short, 102m, 96m, openedAt, symbol, id, null, null);

    private static PaperTrade Open(PositionSideEnum side, decimal stop, decimal target, DateTime? openedAt, string symbol, int id, DateTime? candleOpen, TimeSpan? maxDuration)
    {
        var open = candleOpen ?? CandleOpen;
        var signal = new TradeSignal(
            SignalId.Build("S1", symbol, TimeFrameEnum._4H, open, side), "S1", MarketTypeEnum.Futures, symbol, side, 5m, target, stop, 100m,
            TimeFrameEnum._4H, open, open.AddHours(4), open.AddHours(4).AddMinutes(45), maxDuration);
        var trade = PaperTrade.Open(signal, 100m, openedAt ?? Opened, 0);
        typeof(PaperTrade).GetProperty(nameof(PaperTrade.Id))!.SetValue(trade, id);   // the database gives it; the fake store does not
        return trade;
    }

    /// <summary>The candle of the minute <paramref name="minutesAfter"/> after 08:00.</summary>
    public static Candle Minute(int minutesAfter, decimal open, decimal high, decimal low, decimal close)
    {
        var start = FirstMinute.AddMinutes(minutesAfter);
        return new Candle(start, open, high, low, close, 1m, start.AddMinutes(1).AddMilliseconds(-1), 1m, 1, 1m, 1m);
    }

    /// <summary>Quiet minutes between the levels, from <paramref name="from"/> to <paramref name="to"/> (included).</summary>
    public static IEnumerable<Candle> Quiet(int from, int to) =>
        Enumerable.Range(from, to - from + 1).Select(i => Minute(i, 100m, 101m, 99m, 100m));
}
