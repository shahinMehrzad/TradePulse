using Domain.Enums;

namespace Application.Exchange;

public interface IProbeStreamListener : IStreamListener
{
    /// <summary>A message arrived on the probe stream: proof that the exchange's WebSocket path works.</summary>
    void OnMessage();
}

/// <summary>
/// Cheap, data-free checks that an exchange can be reached right now. The data collectors only talk to an exchange when
/// something closes (a daily candle is one request a day), so their traffic cannot tell whether the connection is alive.
/// </summary>
public interface IExchangeProbe
{
    /// <summary>Pings the exchange's REST API of <paramref name="market"/>. The value is how long the round trip took.</summary>
    Task<ExchangeResult<TimeSpan>> PingAsync(ExchangeEnum exchange, MarketTypeEnum market, CancellationToken cancellationToken);

    /// <summary>
    /// Subscribes to a stream that is busy by nature (a perpetual's mark price ticks every few seconds), so that a message arriving
    /// proves the WebSocket path. A failure is a result, not an exception. Disposing the returned handle unsubscribes.
    /// </summary>
    Task<ExchangeResult<IAsyncDisposable>> SubscribeToProbeStreamAsync(
        ExchangeEnum exchange, SymbolEnum symbol, IProbeStreamListener listener, CancellationToken cancellationToken);
}
