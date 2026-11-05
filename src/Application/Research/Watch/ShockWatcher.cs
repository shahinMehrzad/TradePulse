using System.Collections.Concurrent;
using System.Threading.Channels;
using Application.Exchange;
using Application.Notifications;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Domain.Services;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Research.Watch;

/// <summary>One symbol the watch follows, and the listing its alerts are stored under.</summary>
public sealed record WatchedSymbol(ExchangeEnum Exchange, MarketTypeEnum MarketType, int SymbolListingId, ExchangeSymbol Symbol);

/// <summary>
/// Follows the live trades of the symbols it is given and tells the admin the moment the price of one moves far more than it usually
/// does. The list of symbols may change while it runs (the admin switches symbols on and off): every
/// <see cref="ShockWatchOptions.WatchListRefresh"/> it is compared with what is followed, and only the symbols that changed are
/// started or stopped, so the others keep what they have learned.
/// <para>
/// Nothing here may be slow: a socket callback only puts a trade in a bounded queue, one task judges them in order, one task
/// subscribes and unsubscribes, and another sends the messages and stores the alerts, so a slow Telegram, database or exchange never
/// holds up the trades. A watch that cannot see a symbol is told to the admin too, because its silence looks exactly like a calm market.
/// </para>
/// </summary>
public sealed class ShockWatcher(
    ILiveTradeStream stream,
    IShockAlertStore store,
    ICandleRepository candleRepository,
    IEnumerable<INotifier> notifiers,
    ShockWatchOptions options,
    TimeProvider time,
    ILogger<ShockWatcher> logger)
{
    private const int TradeQueueSize = 200_000;
    private const int OutgoingQueueSize = 1_000;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private enum ItemKind { Trade, Tick }

    private readonly record struct Item(ItemKind Kind, ExchangeSymbol Symbol, LiveTrade Trade);

    /// <summary>A change of the followed symbols, handed from the task that subscribes to the task that judges.</summary>
    private readonly record struct Change(Tracked? Add, ExchangeSymbol Remove);

    /// <summary>
    /// What the admin is told and what is kept: the words (null when the alert is only to be kept, because its symbol was told about recently or
    /// because it only ends), and the alert to keep (null for a message about the watch itself).
    /// </summary>
    private readonly record struct Outgoing(Alert? Message, ShockAlert? ToStore);

    /// <summary>Everything the judging task knows about one followed symbol.</summary>
    private sealed class Tracked(WatchedSymbol symbol, ShockDetector detector, Listener listener)
    {
        public WatchedSymbol Symbol { get; } = symbol;
        public ShockDetector Detector { get; } = detector;
        public Listener Listener { get; } = listener;
        public bool BlindTold { get; set; }

        /// <summary>When the admin was last told about this symbol (a move); null if never since the watch started.</summary>
        public DateTimeOffset? LastMessage { get; set; }
    }

    /// <summary>A symbol that is subscribed. Only the task that subscribes touches these.</summary>
    private sealed record Subscribed(WatchedSymbol Symbol, IAsyncDisposable Handle);

    /// <summary>A fixed list (the command line). Same as <see cref="RunAsync(IWatchedSymbolSource, CancellationToken)"/> with a list that never changes.</summary>
    public Task RunAsync(IReadOnlyList<WatchedSymbol> symbols, CancellationToken cancellationToken) =>
        RunAsync(new FixedWatchedSymbols(symbols), cancellationToken);

    /// <summary>Runs until cancelled. Nothing but cancellation ends it: a failure to connect or to read the list is retried, not thrown.</summary>
    public async Task RunAsync(IWatchedSymbolSource source, CancellationToken cancellationToken)
    {
        var trades = Channel.CreateBounded<Item>(new BoundedChannelOptions(TradeQueueSize) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        var outgoing = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(OutgoingQueueSize) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        var changes = new ConcurrentQueue<Change>();

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sender = Task.Run(() => SendAsync(outgoing.Reader));
        try
        {
            var following = FollowListAsync(source, trades.Writer, changes, outgoing.Writer, linked.Token);
            var judging = JudgeAsync(trades, changes, outgoing.Writer, linked.Token);

            // If one of the two fails for any reason other than being stopped, the other must not go on alone.
            await Task.WhenAny(following, judging);
            await linked.CancelAsync();
            await Task.WhenAll(following, judging);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The normal way out.
        }
        finally
        {
            outgoing.Writer.TryComplete();
            await sender;
        }
    }

    // ---- the task that subscribes ------------------------------------------------------------------------------------------

    private async Task FollowListAsync(
        IWatchedSymbolSource source, ChannelWriter<Item> trades, ConcurrentQueue<Change> changes, ChannelWriter<Outgoing> outgoing, CancellationToken cancellationToken)
    {
        var subscribed = new Dictionary<string, Subscribed>(StringComparer.OrdinalIgnoreCase);
        var failing = new Dictionary<string, (DateTimeOffset Since, bool Told)>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var timer = new PeriodicTimer(options.WatchListRefresh, time);
            do
            {
                await ReconcileAsync(source, trades, changes, outgoing, subscribed, failing, cancellationToken);
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        finally
        {
            foreach (var one in subscribed.Values)
                await StopAsync(one, changes);
        }
    }

    private async Task ReconcileAsync(
        IWatchedSymbolSource source, ChannelWriter<Item> trades, ConcurrentQueue<Change> changes, ChannelWriter<Outgoing> outgoing,
        Dictionary<string, Subscribed> subscribed, Dictionary<string, (DateTimeOffset Since, bool Told)> failing, CancellationToken cancellationToken)
    {
        IReadOnlyList<WatchedSymbol> wanted;
        try
        {
            wanted = await source.GetAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database being down must not make the watch forget what it follows: it goes on with the list it has.
            logger.LogWarning("The list of watched symbols could not be read ({Error}). Keeping the list it has", ex.GetType().Name);
            return;
        }

        var wantedByName = wanted.GroupBy(s => s.Symbol.Value, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var name in subscribed.Keys.Where(name => !wantedByName.ContainsKey(name)).ToList())
        {
            await StopAsync(subscribed[name], changes);
            subscribed.Remove(name);
            logger.LogInformation("{Symbol} is no longer watched", name);
        }

        foreach (var name in failing.Keys.Where(name => !wantedByName.ContainsKey(name)).ToList())
            failing.Remove(name);

        foreach (var (name, symbol) in wantedByName.Where(w => !subscribed.ContainsKey(w.Key)))
        {
            var one = await StartAsync(symbol, trades, changes, outgoing, cancellationToken);
            var now = time.GetUtcNow();

            if (one is null)
            {
                var state = failing.TryGetValue(name, out var existing) ? existing : (Since: now, Told: false);
                if (!state.Told && now - state.Since >= options.BlindAfter)
                {
                    state.Told = true;
                    outgoing.TryWrite(new Outgoing(ShockAlertMessage.Blind(name, now - state.Since), null));
                }

                failing[name] = state;
                continue;
            }

            subscribed[name] = one;
            if (failing.Remove(name, out var was) && was.Told)
                outgoing.TryWrite(new Outgoing(ShockAlertMessage.Restored(name, now - was.Since), null));

            logger.LogInformation("Following the trades of {Symbol}", name);
        }
    }

    /// <summary>
    /// Seeds a detector, hands it to the judging task, then subscribes. Null when the exchange said no (tried again next time).
    /// A symbol that starts without the estimate is told to the admin once it is subscribed (not at every failed try): its silence for
    /// the next hour would look like a calm market.
    /// </summary>
    private async Task<Subscribed?> StartAsync(
        WatchedSymbol symbol, ChannelWriter<Item> trades, ConcurrentQueue<Change> changes, ChannelWriter<Outgoing> outgoing, CancellationToken cancellationToken)
    {
        var detector = new ShockDetector(symbol.SymbolListingId, options);
        var seeded = await SeedAsync(symbol, detector, cancellationToken);

        var listener = new Listener(trades, time);

        // Known to the judging task before the first trade can arrive.
        changes.Enqueue(new Change(new Tracked(symbol, detector, listener), default));

        var result = await stream.SubscribeAsync(symbol.Exchange, symbol.MarketType, [symbol.Symbol], listener, cancellationToken);
        if (result.Success)
        {
            if (!seeded)
                outgoing.TryWrite(new Outgoing(ShockAlertMessage.WarmingUp(symbol.Symbol.Value, TimeSpan.FromSeconds(options.BaselineSeconds)), null));

            return new Subscribed(symbol, result.Value);
        }

        changes.Enqueue(new Change(null, symbol.Symbol));
        logger.LogWarning("{Symbol}: the trade stream could not be subscribed: {Error}. Trying again in {Seconds} s",
            symbol.Symbol.Value, result.Error, options.WatchListRefresh.TotalSeconds);
        return null;
    }

    private async Task StopAsync(Subscribed one, ConcurrentQueue<Change> changes)
    {
        try
        {
            await one.Handle.DisposeAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning("{Symbol}: unsubscribing failed ({Error})", one.Symbol.Symbol.Value, ex.GetType().Name);
        }

        changes.Enqueue(new Change(null, one.Symbol.Symbol));
    }

    /// <summary>
    /// Tells the detector how much its symbol usually moves in a second, from the 1 minute candles already stored, so it can judge from
    /// the start. A symbol without usable candles is not an error: it listens for a whole baseline first, and the log says so.
    /// </summary>
    /// <returns>True when the detector got an estimate and can judge at once.</returns>
    private async Task<bool> SeedAsync(WatchedSymbol symbol, ShockDetector detector, CancellationToken cancellationToken)
    {
        var name = symbol.Symbol.Value;
        try
        {
            var table = TableNameRegistry.GetCandleTableName(symbol.Exchange, symbol.MarketType, name, TimeFrameEnum._1M);
            var candles = await candleRepository.GetLatestAsync(table, options.SeedMinutes + 1, null, cancellationToken);

            // The usual second is estimated from the last hour of candles; the 24 hours are what the gradual check starts from.
            var lastHour = candles.OrderBy(c => c.TimeRange.OpenTime).TakeLast(ShockWatchOptions.HourMinutes + 1);
            if (ShockSeed.TryEstimate(lastHour, time.GetUtcNow().UtcDateTime, options.SeedMaxAge, options.SeedMinReturns, out var volatility))
            {
                detector.Seed(volatility);
                detector.SeedMinutes(candles.OrderBy(c => c.TimeRange.OpenTime)
                    .Select(c => new MinuteClose((long)(c.TimeRange.OpenTime - DateTime.UnixEpoch).TotalMinutes, c.Close)));
                logger.LogInformation("{Symbol} starts from the 1 minute candles: a second usually moves {Volatility:P4}", name, volatility);
                return true;
            }

            logger.LogWarning("{Symbol}: no recent 1 minute candles to start from. It will be silent for {Seconds} s while it listens", name, options.BaselineSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The database or the table being unavailable must not stop the watch; it just starts without the estimate.
            logger.LogWarning("{Symbol}: the 1 minute candles could not be read ({Error}). It will be silent for {Seconds} s while it listens",
                name, ex.GetType().Name, options.BaselineSeconds);
        }

        return false;
    }

    // ---- the task that judges ----------------------------------------------------------------------------------------------

    private async Task JudgeAsync(Channel<Item> trades, ConcurrentQueue<Change> changes, ChannelWriter<Outgoing> outgoing, CancellationToken cancellationToken)
    {
        var tracked = new Dictionary<string, Tracked>(StringComparer.OrdinalIgnoreCase);
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var signals = new List<ShockSignal>();

        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        var ticks = Task.Run(async () =>
        {
            while (await ticker.WaitForNextTickAsync(cancellationToken))
                trades.Writer.TryWrite(new Item(ItemKind.Tick, default, default));
        }, cancellationToken);

        await foreach (var item in trades.Reader.ReadAllAsync(cancellationToken))
        {
            Apply(changes, tracked, outgoing, signals);

            if (item.Kind == ItemKind.Trade)
            {
                if (tracked.TryGetValue(item.Symbol.Value, out var one))
                {
                    one.Detector.Add(item.Trade, signals);
                    Dispatch(one, signals, outgoing);
                }
                else if (unknown.Add(item.Symbol.Value))
                {
                    logger.LogWarning("Ignoring trades of {Symbol}: it is not watched", item.Symbol.Value);
                }
            }
            else
            {
                var now = time.GetUtcNow();
                foreach (var one in tracked.Values)
                {
                    one.Detector.Advance(now.UtcDateTime, signals);
                    Dispatch(one, signals, outgoing);
                    CheckConnection(one, now, outgoing);
                }
            }
        }

        await ticks;
    }

    /// <summary>Takes in the symbols the subscribing task added or dropped. A dropped symbol ends its running alert first.</summary>
    private void Apply(ConcurrentQueue<Change> changes, Dictionary<string, Tracked> tracked, ChannelWriter<Outgoing> outgoing, List<ShockSignal> signals)
    {
        while (changes.TryDequeue(out var change))
        {
            if (change.Add is { } added)
            {
                tracked[added.Symbol.Symbol.Value] = added;
            }
            else if (tracked.Remove(change.Remove.Value, out var removed))
            {
                removed.Detector.Close(signals);
                Dispatch(removed, signals, outgoing);
            }
        }
    }

    private void CheckConnection(Tracked one, DateTimeOffset now, ChannelWriter<Outgoing> outgoing)
    {
        var name = one.Symbol.Symbol.Value;

        var dropped = one.Listener.TakeDropped();
        if (dropped > 0)
            logger.LogWarning("The trade queue was full: {Count} trades were not looked at", dropped);

        if (one.Listener.LostAt is { } lostAt && !one.BlindTold && now - lostAt >= options.BlindAfter)
        {
            one.BlindTold = true;
            outgoing.TryWrite(new Outgoing(ShockAlertMessage.Blind(name, now - lostAt), null));
        }

        if (one.Listener.TakeRestored() is { } offlineFor && one.BlindTold)
        {
            one.BlindTold = false;
            outgoing.TryWrite(new Outgoing(ShockAlertMessage.Restored(name, offlineFor), null));
        }
    }

    /// <summary>
    /// Every alert is kept. Only the start of one is told, and only if the admin was not told about this symbol in the last
    /// <see cref="ShockWatchOptions.SilenceAfterMessage"/>: a symbol cannot fill the chat, however often it moves. The end of an alert only
    /// completes what is kept.
    /// </summary>
    private void Dispatch(Tracked one, List<ShockSignal> signals, ChannelWriter<Outgoing> outgoing)
    {
        foreach (var signal in signals)
        {
            Alert? message = null;
            if (signal.Kind == ShockSignalKind.Started)
            {
                var now = time.GetUtcNow();
                if (one.LastMessage is not { } last || now - last >= options.SilenceAfterMessage)
                {
                    message = ShockAlertMessage.Create(signal.Alert, one.Symbol.Symbol.Value);
                    one.LastMessage = now;
                }
                else
                {
                    logger.LogInformation("{Symbol}: an alert was kept but not told (told {Minutes:0} min ago)", one.Symbol.Symbol.Value, (now - last).TotalMinutes);
                }
            }

            outgoing.TryWrite(new Outgoing(message, signal.Alert));
        }

        signals.Clear();
    }

    // ---- the task that sends -----------------------------------------------------------------------------------------------

    private async Task SendAsync(ChannelReader<Outgoing> reader)
    {
        await foreach (var message in reader.ReadAllAsync())
        {
            using var timeout = new CancellationTokenSource(SendTimeout, time);

            if (message.Message is { } words)
            {
                foreach (var notifier in notifiers)
                {
                    try
                    {
                        await notifier.SendAsync(words, timeout.Token);
                    }
                    catch (Exception ex)
                    {
                        // A NotificationException is written to be told ("Telegram answered 400: group chat was upgraded ..."): the notifier
                        // keeps its token out of it. Any other exception may carry the address it called, so only its kind is logged.
                        logger.LogWarning("{Notifier} could not send an alert ({Error})", notifier.Name, FailureText(ex));
                    }
                }
            }

            if (message.ToStore is null)
                continue;

            try
            {
                await store.SaveAsync(message.ToStore, timeout.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning("An alert could not be stored ({Error})", ex.GetType().Name);
            }
        }
    }

    /// <summary>What may be written to a log about a failed send: the text of a <see cref="NotificationException"/>, the kind of anything else.</summary>
    public static string FailureText(Exception ex) => ex is NotificationException ? ex.Message : ex.GetType().Name;

    /// <summary>The socket's callbacks for one symbol. They only record; the judging happens on the judging task.</summary>
    private sealed class Listener(ChannelWriter<Item> trades, TimeProvider time) : ILiveTradeListener
    {
        private long _lostAtTicks;        // UTC ticks while disconnected, 0 while connected
        private long _restoredAfterTicks; // set when the connection came back, 0 once read
        private long _dropped;

        public DateTimeOffset? LostAt => Interlocked.Read(ref _lostAtTicks) is var ticks and not 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;

        public void OnTrade(ExchangeSymbol symbol, LiveTrade trade)
        {
            if (!trades.TryWrite(new Item(ItemKind.Trade, symbol, trade)))
                Interlocked.Increment(ref _dropped);
        }

        public void OnConnectionLost() =>
            Interlocked.CompareExchange(ref _lostAtTicks, time.GetUtcNow().UtcTicks, 0);

        public void OnConnectionRestored(TimeSpan offlineFor)
        {
            Interlocked.Exchange(ref _lostAtTicks, 0);
            Interlocked.Exchange(ref _restoredAfterTicks, Math.Max(1, offlineFor.Ticks));
        }

        public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

        public TimeSpan? TakeRestored() =>
            Interlocked.Exchange(ref _restoredAfterTicks, 0) is var ticks and not 0 ? TimeSpan.FromTicks(ticks) : null;
    }
}
