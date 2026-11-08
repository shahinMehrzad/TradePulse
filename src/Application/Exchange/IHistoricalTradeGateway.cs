using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Exchange;

/// <summary>
/// The trades an exchange already made, from its request/response API: what <see cref="ILiveTradeStream"/> cannot give because it only
/// delivers what happens while subscribed. Used for the rare minute whose candle holds both the target and the stop of a trade, to replay
/// that minute's trades in order and know which came first, instead of assuming.
/// </summary>
public interface IHistoricalTradeGateway
{
    /// <summary>
    /// Every trade of <paramref name="symbol"/> from <paramref name="from"/> (included) to <paramref name="to"/> (not included), oldest first,
    /// as the exchange aggregates them. The adapter pages through the answer and respects the exchange's request budget; a failure at any
    /// page is a failed result (never a part of the minute: a replay of a part would be a guess).
    /// </summary>
    Task<ExchangeResult<IReadOnlyList<LiveTrade>>> GetTradesAsync(
        ExchangeEnum exchange, MarketTypeEnum marketType, ExchangeSymbol symbol, DateTime from, DateTime to, CancellationToken cancellationToken);
}
