namespace Application.Exchange;

/// <summary>
/// What a collector wants to hear about a live (WebSocket) connection besides the data itself. The connection is kept alive and
/// reconnected by the exchange adapter; the collector is only told, so it can report health and make up for what was missed.
/// All methods are called on the connection's own thread: they must stay cheap and must never block.
/// </summary>
public interface IStreamListener
{
    /// <summary>The connection dropped; the adapter is already reconnecting.</summary>
    void OnConnectionLost();

    /// <summary>The connection is back after <paramref name="offlineFor"/>. Whatever arrived meanwhile was missed.</summary>
    void OnConnectionRestored(TimeSpan offlineFor);
}
