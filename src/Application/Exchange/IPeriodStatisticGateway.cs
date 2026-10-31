using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Exchange;

/// <summary>
/// A statistic an exchange publishes once per period for a perpetual futures symbol (long/short ratio, taker volume, ...), as rows
/// of <typeparamref name="TRow"/>. All of them share the shape of open interest: a time frame out of a short list, a page of rows, and
/// only a limited history.
/// </summary>
public interface IPeriodStatisticGateway<TRow>
{
    /// <summary>
    /// How far back the exchange still serves rows. Anything older than the first run can never be recovered, which is why the
    /// collector must run continuously.
    /// </summary>
    TimeSpan MaxHistory { get; }

    /// <summary>
    /// One page of rows of <paramref name="symbol"/> for every <paramref name="timeFrame"/>, oldest first, at or after
    /// <paramref name="from"/>. Only the time frames <c>MarketDataSupport</c> lists for the statistic are served; any other is a
    /// mistake of the caller and throws. <see cref="ExchangePage{T}.HasMore"/> says whether to ask again from after the last one.
    /// </summary>
    Task<ExchangeResult<ExchangePage<TRow>>> GetAsync(
        ExchangeEnum exchange, ExchangeSymbol symbol, TimeFrameEnum timeFrame, DateTime from, CancellationToken cancellationToken);
}
