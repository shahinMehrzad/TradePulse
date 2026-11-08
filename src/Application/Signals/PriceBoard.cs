using System.Collections.Concurrent;
using Domain.Enums;

namespace Application.Signals;

/// <summary>The latest price seen on the exchange and when the trade that made it happened (the exchange's time, UTC).</summary>
public sealed record LivePrice(decimal Price, DateTime At);

/// <summary>
/// The latest trade price of every followed symbol. The price watch writes it for every trade; whoever takes a signal reads it, to have a
/// price to enter at and to judge the signal against. A price older than the caller allows is no price: it is said not to be there.
/// </summary>
public sealed class PriceBoard
{
    private readonly ConcurrentDictionary<(ExchangeEnum, string), LivePrice> _latest = new();

    public void Set(ExchangeEnum exchange, string symbol, decimal price, DateTime at) => _latest[(exchange, symbol)] = new LivePrice(price, at);

    /// <summary>The latest price of the symbol, or null when none was seen or the last one is older than <paramref name="maxAge"/> at <paramref name="now"/>.</summary>
    public LivePrice? Get(ExchangeEnum exchange, string symbol, DateTime now, TimeSpan maxAge) =>
        _latest.TryGetValue((exchange, symbol), out var latest) && now - latest.At <= maxAge ? latest : null;
}
