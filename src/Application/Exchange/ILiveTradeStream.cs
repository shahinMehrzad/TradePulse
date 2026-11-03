using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Exchange;

/// <summary>One trade (or a run of trades at one price, as the exchange aggregates them).</summary>
/// <param name="Time">UTC, when the trade happened on the exchange.</param>
/// <param name="Quantity">In the base asset.</param>
public readonly record struct LiveTrade(DateTime Time, decimal Price, decimal Quantity);

public interface ILiveTradeListener : IStreamListener
{
    /// <summary>A trade of <paramref name="symbol"/> happened. Called for every trade: stay cheap, never block.</summary>
    void OnTrade(ExchangeSymbol symbol, LiveTrade trade);
}

/// <summary>
/// The trades of an exchange market pushed over a WebSocket the moment they happen. The exchange sells no candles below one minute
/// for futures, so anything shorter is built from these. There is no history to ask for: only what arrives while subscribed is seen.
/// </summary>
public interface ILiveTradeStream
{
    /// <summary>
    /// Subscribes once. A failure is a result, not an exception; whether and when to try again is the caller's policy.
    /// Disposing the returned handle unsubscribes.
    /// </summary>
    Task<ExchangeResult<IAsyncDisposable>> SubscribeAsync(
        ExchangeEnum exchange, MarketTypeEnum marketType, IReadOnlyList<ExchangeSymbol> symbols, ILiveTradeListener listener, CancellationToken cancellationToken);
}
