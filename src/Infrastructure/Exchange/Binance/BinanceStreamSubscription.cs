using Application.Exchange;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Objects.Sockets;

namespace Infrastructure.Exchange.Binance;

/// <summary>Ties a Binance.Net socket subscription to a listener, and unsubscribes (this one only) when disposed.</summary>
internal sealed class BinanceStreamSubscription : IAsyncDisposable
{
    private readonly IBinanceSocketClient _client;
    private readonly UpdateSubscription _subscription;

    /// <summary>
    /// The library reconnects on its own; these two events are all the listener needs to know about it. They are attached right
    /// after subscribing, as before this was behind a port.
    /// </summary>
    public BinanceStreamSubscription(IBinanceSocketClient client, UpdateSubscription subscription, IStreamListener listener)
    {
        _client = client;
        _subscription = subscription;

        subscription.ConnectionLost += listener.OnConnectionLost;
        subscription.ConnectionRestored += listener.OnConnectionRestored;
    }

    public async ValueTask DisposeAsync() => await _client.UnsubscribeAsync(_subscription);
}
