using System.Collections.Concurrent;
using System.Threading.Channels;
using Application.Exchange;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Signals;

/// <summary>
/// Follows the price of every open paper trade and ends it when the target or the stop is reached. It is the one thing that keeps looking:
/// a trade stays open for days, and the service restarts in that time.
/// <para>
/// Every trade of every followed symbol is looked at, in order, by one task, so nothing here needs a lock: the socket's callbacks only put a
/// trade into a bounded queue. What nobody saw (a restart, a lost connection, a full queue, the first half of the minute a subscription starts
/// in) is looked at afterwards through the exchange's 1 minute candles by <see cref="TradeCatchUp"/>, and never guessed. A trade ends once: the
/// store changes its row only if it is still open, and only the caller that changed it tells the rest of the service.
/// </para>
/// <para>
/// The symbols are the listed ones (<see cref="IFollowedSymbolSource"/>) and every symbol that has an open trade, even if it was unlisted
/// since. <see cref="Track"/> hands over a trade that has just opened.
/// </para>
/// </summary>
public sealed class PaperTradeWatcher(
    ILiveTradeStream stream,
    ISignalStore store,
    IFollowedSymbolSource source,
    TradeCatchUp catchUp,
    ITradeEvents events,
    PriceBoard board,
    WatcherOptions options,
    TimeProvider time,
    ILogger<PaperTradeWatcher> logger) : ITradeTracker
{
    private enum ItemKind { Trade, Wake, Tick }

    private readonly record struct Item(ItemKind Kind, ExchangeEnum Exchange, ExchangeSymbol Symbol, LiveTrade Trade);

    /// <summary>A look at the past still to be done. <c>Key</c> null: every symbol. <c>From</c> null: from where each trade had been followed.</summary>
    private sealed record Job(DateTime Due, (ExchangeEnum Exchange, string Symbol)? Key, DateTime? From);

    private sealed record Subscription(FollowedSymbol Symbol, IAsyncDisposable Handle, Listener Listener);

    private readonly Channel<Item> _queue = Channel.CreateBounded<Item>(
        new BoundedChannelOptions(options.QueueSize) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly ConcurrentQueue<PaperTrade> _added = new();

    // Everything below belongs to the one task that looks at the trades.
    private readonly Dictionary<(ExchangeEnum Exchange, string Symbol), List<PaperTrade>> _open = [];
    private readonly Dictionary<(ExchangeEnum Exchange, string ExchangeSymbol), Subscription> _subscribed = [];
    private readonly Dictionary<(ExchangeEnum Exchange, string Symbol), FollowedSymbol> _known = [];
    private readonly List<PaperTrade> _endedNotStored = [];
    private readonly List<Job> _jobs = [];
    private IReadOnlyList<FollowedSymbol> _listed = [];
    private DateTime _nextReconcile;
    private DateTime _nextSave;

    /// <summary>Hands over a trade that has just opened and has been stored. Safe to call from any thread, at any time.</summary>
    public void Track(PaperTrade trade)
    {
        _added.Enqueue(trade);
        _queue.Writer.TryWrite(new Item(ItemKind.Wake, default, default, default));
    }

    /// <summary>Runs until cancelled. Nothing but cancellation ends it: a database or an exchange that does not answer is tried again.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadOpenTradesAsync(cancellationToken);

            var now = Now();
            _jobs.Add(new Job(now, null, null));
            _nextReconcile = now;
            _nextSave = now + options.ProgressSave;
            await HousekeepingAsync(cancellationToken);

            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
            var ticks = Task.Run(async () =>
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                    _queue.Writer.TryWrite(new Item(ItemKind.Tick, default, default, default));
            }, cancellationToken);

            await foreach (var item in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                DrainAdded();

                if (item.Kind == ItemKind.Trade)
                    await OnTradeAsync(item, cancellationToken);
                else if (item.Kind == ItemKind.Tick)
                    await HousekeepingAsync(cancellationToken);
            }

            await ticks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The normal way out.
        }
        finally
        {
            foreach (var one in _subscribed.Values)
                await StopAsync(one);
        }
    }

    private DateTime Now() => time.GetUtcNow().UtcDateTime;

    // ---- the trades ----------------------------------------------------------------------------------------------------------

    private async Task LoadOpenTradesAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                foreach (var trade in await store.GetOpenTradesAsync(cancellationToken))
                    Add(trade);

                logger.LogInformation("Following {Count} open paper trade(s)", _open.Values.Sum(l => l.Count));
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("The open trades could not be read ({Error}). Trying again in {Seconds} s", ex.GetType().Name, options.StartRetry.TotalSeconds);
                await Task.Delay(options.StartRetry, time, cancellationToken);
            }
        }
    }

    private void Add(PaperTrade trade)
    {
        var key = (trade.Exchange, trade.Symbol);
        if (!_open.TryGetValue(key, out var list))
            _open[key] = list = [];

        // A trade handed over while the open ones were being read may be read as well: it is followed once.
        if (trade.Id == 0 || list.All(t => t.Id != trade.Id))
            list.Add(trade);
    }

    /// <summary>Takes in the trades handed over with <see cref="Track"/>. Their symbol is subscribed on the next round if it is not already.</summary>
    private void DrainAdded()
    {
        while (_added.TryDequeue(out var trade))
        {
            Add(trade);
            _nextReconcile = DateTime.MinValue;
        }
    }

    private async Task OnTradeAsync(Item item, CancellationToken cancellationToken)
    {
        if (!_subscribed.TryGetValue((item.Exchange, item.Symbol.Value), out var one))
            return;

        var name = one.Symbol.Symbol;
        board.Set(item.Exchange, name, item.Trade.Price, item.Trade.Time);

        if (!_open.TryGetValue((item.Exchange, name), out var trades))
            return;

        // A copy: ending a trade removes it from the list being walked.
        foreach (var trade in trades.ToArray())
        {
            trade.MarkChecked(item.Trade.Time);
            if (trade.Observe(item.Trade.Price, item.Trade.Time))
                await FinishAsync(trade, cancellationToken);
        }
    }

    /// <summary>The trade has ended in memory. It is stored as ended, and only if that changed its row is the rest of the service told.</summary>
    private async Task FinishAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        if (_open.TryGetValue((trade.Exchange, trade.Symbol), out var list))
        {
            list.Remove(trade);
            if (list.Count == 0)
                _open.Remove((trade.Exchange, trade.Symbol));
        }

        await StoreEndAsync(trade, cancellationToken);
    }

    /// <returns>False when the database could not be reached: the trade is kept to be stored again.</returns>
    private async Task<bool> StoreEndAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        bool changed;
        try
        {
            changed = await store.CloseAsync(trade, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // It ended; that must not be lost because the database was busy. It stays in memory, ended, until it is stored.
            if (!_endedNotStored.Contains(trade))
                _endedNotStored.Add(trade);

            logger.LogWarning("The end of the trade {Id} could not be stored ({Error}). Trying again", trade.Id, ex.GetType().Name);
            return false;
        }

        _endedNotStored.Remove(trade);

        if (!changed)
        {
            logger.LogInformation("The trade {Id} was already ended by someone else", trade.Id);
            return true;
        }

        logger.LogInformation("The trade {Id} ({Symbol} {Side}) ended: {Status} at {Price}", trade.Id, trade.Symbol, trade.Side, trade.Status, trade.ExitPrice);
        try
        {
            await events.OnClosedAsync(trade, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError("Telling about the end of the trade {Id} failed ({Error})", trade.Id, ex.GetType().Name);
        }

        return true;
    }

    // ---- once a second -------------------------------------------------------------------------------------------------------

    private async Task HousekeepingAsync(CancellationToken cancellationToken)
    {
        var now = Now();

        foreach (var trade in _endedNotStored.ToArray())
            await StoreEndAsync(trade, cancellationToken);

        CollectWhatWasMissed(now);

        if (now >= _nextReconcile)
        {
            var allSubscribed = await ReconcileAsync(cancellationToken);
            _nextReconcile = now + (allSubscribed ? options.SymbolRefresh : options.SubscribeRetry);
        }

        await RunDueJobsAsync(now, cancellationToken);

        if (now >= _nextSave)
        {
            await SaveProgressAsync(cancellationToken);
            _nextSave = now + options.ProgressSave;
        }
    }

    /// <summary>A connection that came back, or trades that did not fit the queue, are stretches nobody looked at: a look at the past is due.</summary>
    private void CollectWhatWasMissed(DateTime now)
    {
        foreach (var one in _subscribed.Values)
        {
            if (one.Listener.TakeRestored() is { } offlineFor)
            {
                logger.LogInformation("{Symbol}: the connection is back after {Seconds:0} s; looking at what it missed", one.Symbol.Symbol, offlineFor.TotalSeconds);
                _jobs.Add(new Job(now, one.Symbol.Key, now - offlineFor - options.CatchUpMargin));
            }

            if (one.Listener.TakeDroppedFrom() is { } from)
            {
                logger.LogError("{Symbol}: the queue was full and trades were not looked at; looking at that stretch again from the candles", one.Symbol.Symbol);
                _jobs.Add(new Job(now, one.Symbol.Key, from - options.CatchUpMargin));
            }
        }
    }

    private async Task RunDueJobsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var due = _jobs.Where(j => j.Due <= now).ToList();
        if (due.Count == 0)
            return;

        _jobs.RemoveAll(due.Contains);

        // One look per symbol, from the earliest moment any of its jobs asked for (null: from where each trade had been followed).
        var keys = due.Any(j => j.Key is null) ? _open.Keys.ToList() : due.Select(j => j.Key!.Value).Distinct().ToList();
        foreach (var key in keys)
        {
            if (!_open.TryGetValue(key, out var trades) || trades.Count == 0)
                continue;

            var explicitFrom = due.Where(j => (j.Key is null || j.Key == key) && j.From is not null).Select(j => j.From!.Value).DefaultIfEmpty().Min();
            DateTime? from = explicitFrom == default ? null : explicitFrom;

            var ended = new List<PaperTrade>();
            var complete = false;
            try
            {
                complete = await catchUp.RunAsync(SymbolOf(key), trades.ToArray(), from, ended, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError("{Symbol}: looking at the past failed ({Error}). Trying again", key.Symbol, ex.GetType().Name);
            }

            foreach (var trade in ended)
                await FinishAsync(trade, cancellationToken);

            if (!complete)
                _jobs.Add(new Job(now + options.CatchUpRetry, key, from));
        }
    }

    private FollowedSymbol SymbolOf((ExchangeEnum Exchange, string Symbol) key) =>
        _known.TryGetValue(key, out var known) ? known : FollowedSymbol.Guess(key.Exchange, key.Symbol);

    private async Task SaveProgressAsync(CancellationToken cancellationToken)
    {
        var open = _open.Values.SelectMany(l => l).Where(t => t.IsOpen).ToList();
        if (open.Count == 0)
            return;

        try
        {
            await store.SaveProgressAsync(open, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Only a restart would feel it: it would look at a little more of the past.
            logger.LogWarning("The progress of the open trades could not be saved ({Error})", ex.GetType().Name);
        }
    }

    // ---- what is subscribed --------------------------------------------------------------------------------------------------

    /// <returns>False when a symbol could not be subscribed: it is tried again soon, not after the usual refresh.</returns>
    private async Task<bool> ReconcileAsync(CancellationToken cancellationToken)
    {
        var allSubscribed = true;

        try
        {
            _listed = await source.GetAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database being down must not make it forget what it follows: it goes on with the list it has.
            logger.LogWarning("The list of followed symbols could not be read ({Error}). Keeping the list it has", ex.GetType().Name);
        }

        foreach (var symbol in _listed)
            _known[symbol.Key] = symbol;

        // The listed symbols, and every symbol with an open trade (listed or not any more).
        var wanted = _listed.ToDictionary(s => (s.Exchange, s.ExchangeSymbol.Value));
        foreach (var key in _open.Keys)
        {
            var symbol = SymbolOf(key);
            wanted.TryAdd((symbol.Exchange, symbol.ExchangeSymbol.Value), symbol);
        }

        foreach (var key in _subscribed.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            await StopAsync(_subscribed[key]);
            _subscribed.Remove(key);
            logger.LogInformation("{Symbol} is no longer followed", key.ExchangeSymbol);
        }

        foreach (var (key, symbol) in wanted.Where(w => !_subscribed.ContainsKey(w.Key)))
        {
            var listener = new Listener(symbol.Exchange, _queue.Writer);
            var result = await stream.SubscribeAsync(symbol.Exchange, symbol.Market, [symbol.ExchangeSymbol], listener, cancellationToken);
            var now = Now();

            if (!result.Success)
            {
                logger.LogWarning("{Symbol}: the trade stream could not be subscribed: {Error}. Trying again in {Seconds} s",
                    symbol.Symbol, result.Error, options.SubscribeRetry.TotalSeconds);
                allSubscribed = false;
                continue;
            }

            _subscribed[key] = new Subscription(symbol, result.Value, listener);
            logger.LogInformation("Following the trades of {Symbol}", symbol.Symbol);

            // Whatever happened to its trades before this moment, and the first half of the minute it starts in: the candle of that minute is asked for once it has closed.
            _jobs.Add(new Job(now, symbol.Key, null));
            var minute = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMinute, DateTimeKind.Utc);
            _jobs.Add(new Job(minute.AddMinutes(1) + options.CandleDelay, symbol.Key, minute));
        }

        return allSubscribed;
    }

    private async Task StopAsync(Subscription one)
    {
        try
        {
            await one.Handle.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning("{Symbol}: unsubscribing failed ({Error})", one.Symbol.Symbol, ex.GetType().Name);
        }
    }

    /// <summary>The socket's callbacks for one symbol. They only record; everything is looked at by the one task.</summary>
    private sealed class Listener(ExchangeEnum exchange, ChannelWriter<Item> queue) : ILiveTradeListener
    {
        private long _restoredAfterTicks;   // set when the connection came back, 0 once read
        private long _droppedFromTicks;     // the time of the first trade that did not fit, 0 once read

        public void OnTrade(ExchangeSymbol symbol, LiveTrade trade)
        {
            if (!queue.TryWrite(new Item(ItemKind.Trade, exchange, symbol, trade)))
                Interlocked.CompareExchange(ref _droppedFromTicks, trade.Time.Ticks, 0);
        }

        public void OnConnectionLost()
        {
            // Nothing to do: what was missed is looked at when it is back.
        }

        public void OnConnectionRestored(TimeSpan offlineFor) =>
            Interlocked.Exchange(ref _restoredAfterTicks, Math.Max(1, offlineFor.Ticks));

        public TimeSpan? TakeRestored() =>
            Interlocked.Exchange(ref _restoredAfterTicks, 0) is var ticks and not 0 ? TimeSpan.FromTicks(ticks) : null;

        public DateTime? TakeDroppedFrom() =>
            Interlocked.Exchange(ref _droppedFromTicks, 0) is var ticks and not 0 ? new DateTime(ticks, DateTimeKind.Utc) : null;
    }
}
