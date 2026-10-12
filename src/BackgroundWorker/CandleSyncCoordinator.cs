using System.Collections.Concurrent;
using System.Threading.Channels;

namespace BackgroundWorker;

/// <summary>
/// Link between the live WebSocket worker and the REST sync loops.
/// <list type="bullet">
/// <item><b>Ready tables:</b> a live candle may only be stored once the REST sync has finished its first pass for that
/// table. Otherwise a live candle in an empty (or stale) table becomes <c>max(open_time)</c> and the next sync would
/// resume after it, silently skipping the history before it.</item>
/// <item><b>Resync:</b> after a socket reconnect the live worker asks every REST loop to sync and repair gaps at once,
/// instead of waiting for the next candle boundary.</item>
/// </list>
/// </summary>
public sealed class CandleSyncCoordinator
{
    private readonly ConcurrentDictionary<string, byte> _ready = new();
    private readonly Lock _gate = new();
    private readonly List<Channel<bool>> _loops = [];

    public bool IsReady(string table) => _ready.ContainsKey(table);

    public void MarkReady(string table) => _ready.TryAdd(table, 0);

    /// <summary>Registers a REST loop; the returned reader yields once per (coalesced) resync request.</summary>
    public ChannelReader<bool> Register()
    {
        var channel = Channel.CreateBounded<bool>(
            new BoundedChannelOptions(1) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

        lock (_gate)
            _loops.Add(channel);

        return channel.Reader;
    }

    /// <summary>Requests an immediate sync + gap repair in every registered loop. Never blocks.</summary>
    public void RequestResync()
    {
        lock (_gate)
        {
            foreach (var loop in _loops)
                loop.Writer.TryWrite(true);
        }
    }
}
