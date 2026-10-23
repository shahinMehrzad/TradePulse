using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Exchange;

/// <summary>One live subscription: the candles of several symbols of one exchange market, all at the same time frame.</summary>
public sealed record LiveCandleSubscription(ExchangeEnum Exchange, MarketTypeEnum MarketType, TimeFrameEnum Timeframe, IReadOnlyList<SymbolEnum> Symbols);

public interface ILiveCandleListener : IStreamListener
{
    /// <summary>Any message at all, closed candle or not. Proves the stream is alive.</summary>
    void OnMessage();

    /// <summary>A candle just closed.</summary>
    void OnClosedCandle(CandleSeries series, Candle candle);
}

/// <summary>Candles pushed by an exchange the moment they close, over a WebSocket.</summary>
public interface ILiveCandleStream
{
    /// <summary>
    /// Subscribes once. A failure (the exchange or the network said no) is a result, not an exception; whether and when to try
    /// again is the caller's policy. Disposing the returned handle unsubscribes.
    /// </summary>
    Task<ExchangeResult<IAsyncDisposable>> SubscribeAsync(
        LiveCandleSubscription subscription, ILiveCandleListener listener, CancellationToken cancellationToken);
}
