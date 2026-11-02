using System.Threading.Channels;
using Application.Exchange;
using Application.Notifications;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Research.Watch;

/// <summary>One symbol the watch follows, and the listing its alerts are stored under.</summary>
public sealed record WatchedSymbol(ExchangeEnum Exchange, MarketTypeEnum MarketType, int SymbolListingId, ExchangeSymbol Symbol);

/// <summary>
/// Follows the live trades of some symbols of one exchange market and tells the admin the moment the price moves far more than it
/// usually does. Nothing here may be slow: the socket callback only puts a trade in a bounded queue, one task judges them in order,
/// and another sends the messages and stores the alerts, so a slow Telegram or database never holds up the trades.
/// <para>
/// A watch that cannot see the market is told to the admin too, because its silence looks exactly like a calm market.
/// </para>
/// </summary>
public sealed class ShockWatcher(
    ILiveTradeStream stream,
    IShockAlertStore store,
    IEnumerable<INotifier> notifiers,
    ShockWatchOptions options,
    TimeProvider time,
    ILogger<ShockWatcher> logger)
{
    private const int TradeQueueSize = 200_000;
    private const int OutgoingQueueSize = 1_000;
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private enum ItemKind { Trade, Tick }

    private readonly record struct Item(ItemKind Kind, ExchangeSymbol Symbol, LiveTrade Trade);

    /// <summary>What goes out: the words for the admin, and the alert to keep (null for a message about the watch itself).</summary>
    private readonly record struct Outgoing(Alert Message, ShockAlert? ToStore);

    /// <summary>Runs until cancelled. Nothing but cancellation ends it: a failure to connect is retried, not thrown.</summary>
    public async Task RunAsync(IReadOnlyList<WatchedSymbol> symbols, CancellationToken cancellationToken)
    {
        if (symbols.Count == 0)
            throw new ArgumentException("At least one symbol must be watched.", nameof(symbols));
        var first = symbols[0];
        if (symbols.Any(s => s.Exchange != first.Exchange || s.MarketType != first.MarketType))
            throw new ArgumentException("One watch follows one exchange market.", nameof(symbols));

        var watched = symbols.ToDictionary(s => s.Symbol.Value, s => (Symbol: s, Detector: new ShockDetector(s.SymbolListingId, options)), StringComparer.OrdinalIgnoreCase);
        var names = string.Join(", ", symbols.Select(s => s.Symbol.Value));

        var trades = Channel.CreateBounded<Item>(new BoundedChannelOptions(TradeQueueSize) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        var outgoing = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(OutgoingQueueSize) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
        var listener = new Listener(trades.Writer, time);

        var sender = Task.Run(() => SendAsync(outgoing.Reader));
        try
        {
            if (!await ConnectAsync(first, symbols, listener, outgoing.Writer, names, cancellationToken))
                return;

            await using var _ = listener.Handle;
            await JudgeAsync(trades, outgoing.Writer, listener, watched, names, cancellationToken);
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

    /// <summary>Subscribes, retrying until it works. Returns false only when cancelled.</summary>
    private async Task<bool> ConnectAsync(
        WatchedSymbol first, IReadOnlyList<WatchedSymbol> symbols, Listener listener, ChannelWriter<Outgoing> outgoing, string names, CancellationToken cancellationToken)
    {
        DateTimeOffset? failingSince = null;
        var told = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var result = await stream.SubscribeAsync(first.Exchange, first.MarketType, symbols.Select(s => s.Symbol).ToList(), listener, cancellationToken);
            if (result.Success)
            {
                listener.Handle = result.Value;
                if (told)
                    outgoing.TryWrite(new Outgoing(ShockAlertMessage.Restored(names, time.GetUtcNow() - failingSince!.Value), null));
                return true;
            }

            logger.LogWarning("The trade stream could not be subscribed: {Error}. Trying again in {Seconds} s", result.Error, RetryAfterFailure.TotalSeconds);
            failingSince ??= time.GetUtcNow();
            if (!told && time.GetUtcNow() - failingSince >= options.BlindAfter)
            {
                told = true;
                outgoing.TryWrite(new Outgoing(ShockAlertMessage.Blind(names, time.GetUtcNow() - failingSince.Value), null));
            }

            await Task.Delay(RetryAfterFailure, time, cancellationToken);
        }

        return false;
    }

    private async Task JudgeAsync(
        Channel<Item> trades, ChannelWriter<Outgoing> outgoing, Listener listener,
        Dictionary<string, (WatchedSymbol Symbol, ShockDetector Detector)> watched, string names, CancellationToken cancellationToken)
    {
        var signals = new List<ShockSignal>();
        var blindTold = false;
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var ticker = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
        var ticks = Task.Run(async () =>
        {
            while (await ticker.WaitForNextTickAsync(cancellationToken))
                trades.Writer.TryWrite(new Item(ItemKind.Tick, default, default));
        }, cancellationToken);

        await foreach (var item in trades.Reader.ReadAllAsync(cancellationToken))
        {
            if (item.Kind == ItemKind.Trade)
            {
                if (watched.TryGetValue(item.Symbol.Value, out var one))
                    one.Detector.Add(item.Trade, signals);
                else if (unknown.Add(item.Symbol.Value))
                    logger.LogWarning("Ignoring trades of {Symbol}: it is not watched", item.Symbol.Value);
            }
            else
            {
                foreach (var (_, detector) in watched.Values)
                    detector.Advance(time.GetUtcNow().UtcDateTime, signals);

                CheckConnection(listener, outgoing, names, ref blindTold);
            }

            if (signals.Count > 0)
            {
                Dispatch(signals, outgoing, watched);
                signals.Clear();
            }
        }

        await ticks;
    }

    private void CheckConnection(Listener listener, ChannelWriter<Outgoing> outgoing, string names, ref bool blindTold)
    {
        var dropped = listener.TakeDropped();
        if (dropped > 0)
            logger.LogWarning("The trade queue was full: {Count} trades were not looked at", dropped);

        var now = time.GetUtcNow();
        if (listener.LostAt is { } lostAt && !blindTold && now - lostAt >= options.BlindAfter)
        {
            blindTold = true;
            outgoing.TryWrite(new Outgoing(ShockAlertMessage.Blind(names, now - lostAt), null));
        }

        if (listener.TakeRestored() is { } offlineFor && blindTold)
        {
            blindTold = false;
            outgoing.TryWrite(new Outgoing(ShockAlertMessage.Restored(names, offlineFor), null));
        }
    }

    private static void Dispatch(
        List<ShockSignal> signals, ChannelWriter<Outgoing> outgoing, Dictionary<string, (WatchedSymbol Symbol, ShockDetector Detector)> watched)
    {
        foreach (var signal in signals)
        {
            var symbol = watched.Values.First(w => w.Symbol.SymbolListingId == signal.Alert.SymbolListingId).Symbol.Symbol.Value;
            outgoing.TryWrite(new Outgoing(ShockAlertMessage.Create(signal.Alert, symbol, signal.Kind), signal.Alert));
        }
    }

    private async Task SendAsync(ChannelReader<Outgoing> reader)
    {
        await foreach (var message in reader.ReadAllAsync())
        {
            using var timeout = new CancellationTokenSource(SendTimeout, time);

            foreach (var notifier in notifiers)
            {
                try
                {
                    await notifier.SendAsync(message.Message, timeout.Token);
                }
                catch (Exception ex)
                {
                    // Only the kind of failure, never its text: a notifier's error may carry the address it called.
                    logger.LogWarning("{Notifier} could not send an alert ({Error})", notifier.Name, ex.GetType().Name);
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

    /// <summary>The socket's callbacks. They only record; the judging happens on the consumer's own task.</summary>
    private sealed class Listener(ChannelWriter<Item> trades, TimeProvider time) : ILiveTradeListener
    {
        private long _lostAtTicks;       // UTC ticks while disconnected, 0 while connected
        private long _restoredAfterTicks; // set when the connection came back, 0 once read
        private long _dropped;

        public IAsyncDisposable? Handle { get; set; }

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
