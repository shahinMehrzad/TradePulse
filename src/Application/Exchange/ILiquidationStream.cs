using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Exchange;

public interface ILiquidationListener : IStreamListener
{
    /// <summary>A forced liquidation order was reported for <paramref name="symbol"/>.</summary>
    void OnLiquidation(ExchangeSymbol symbol, Liquidation liquidation);
}

/// <summary>
/// Forced liquidation orders pushed by an exchange over a WebSocket. There is no history to ask for: only what arrives while
/// subscribed is ever seen, and the exchange may send at most one per symbol per second, so it is a sample.
/// </summary>
public interface ILiquidationStream
{
    /// <summary>
    /// Subscribes once. A failure is a result, not an exception; whether and when to try again is the caller's policy.
    /// Disposing the returned handle unsubscribes.
    /// </summary>
    Task<ExchangeResult<IAsyncDisposable>> SubscribeAsync(
        ExchangeEnum exchange, IReadOnlyList<ExchangeSymbol> symbols, ILiquidationListener listener, CancellationToken cancellationToken);
}
