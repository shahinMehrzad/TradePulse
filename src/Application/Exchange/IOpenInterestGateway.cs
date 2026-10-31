using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Exchange;

/// <summary>Open interest snapshots from an exchange's request/response API (perpetual futures only).</summary>
public interface IOpenInterestGateway
{
    /// <summary>
    /// How far back the exchange still serves snapshots. Anything older than the first run can never be recovered, which is why
    /// the collector must run continuously.
    /// </summary>
    TimeSpan MaxHistory { get; }

    /// <summary>
    /// One page of snapshots of <paramref name="symbol"/> taken every <paramref name="timeFrame"/>, oldest first, at or after
    /// <paramref name="from"/>. Only the time frames <c>MarketDataSupport</c> lists for open interest are served; any other is a
    /// mistake of the caller and throws. <see cref="ExchangePage{T}.HasMore"/> says whether to ask again from after the last one.
    /// </summary>
    Task<ExchangeResult<ExchangePage<OpenInterest>>> GetOpenInterestAsync(
        ExchangeEnum exchange, ExchangeSymbol symbol, TimeFrameEnum timeFrame, DateTime from, CancellationToken cancellationToken);
}
