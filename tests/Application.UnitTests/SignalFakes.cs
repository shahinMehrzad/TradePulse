using Application.Exchange;
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

    public Task<bool> RecordAsync(ReceivedSignal signal, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<int> CountOpenAsync(ExchangeEnum exchange, string strategy, string symbol, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task AddTradeAsync(PaperTrade trade, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<bool> AttachMessageAsync(int tradeId, long chatId, int messageId, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task AddEventAsync(SignalEvent signalEvent, CancellationToken cancellationToken) => throw new NotSupportedException();
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
    public static PaperTrade Long(DateTime? openedAt = null, string symbol = "BTC/USDT", int id = 1, DateTime? candleOpen = null) => Open(PositionSideEnum.Long, 98m, 104m, openedAt, symbol, id, candleOpen);

    /// <summary>A short at 100: target 96, stop 102.</summary>
    public static PaperTrade Short(DateTime? openedAt = null, string symbol = "BTC/USDT", int id = 1) => Open(PositionSideEnum.Short, 102m, 96m, openedAt, symbol, id, null);

    private static PaperTrade Open(PositionSideEnum side, decimal stop, decimal target, DateTime? openedAt, string symbol, int id, DateTime? candleOpen)
    {
        var open = candleOpen ?? CandleOpen;
        var signal = new TradeSignal(
            SignalId.Build("S1", symbol, TimeFrameEnum._4H, open, side), "S1", MarketTypeEnum.Futures, symbol, side, 5m, target, stop, 100m,
            TimeFrameEnum._4H, open, open.AddHours(4), open.AddHours(4).AddMinutes(45));
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
