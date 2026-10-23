using Domain.Entities;
using Domain.ValueObjects;

namespace Application.Exchange;

/// <summary>
/// Candles from an exchange's request/response API. Everything exchange-specific lives behind it: the symbol and interval
/// names, the page size, and the request budget (the exchange's rate limit is respected before each call).
/// </summary>
public interface ICandleGateway
{
    /// <summary>
    /// One page of candles of <paramref name="series"/>, oldest first, whose open time is at or after <paramref name="from"/> (and at
    /// or before <paramref name="to"/>, inclusive, when given). The newest candle may still be open: deciding which are closed is
    /// the caller's. <see cref="ExchangePage{T}.HasMore"/> says whether to ask again from after the last one.
    /// </summary>
    Task<ExchangeResult<ExchangePage<Candle>>> GetCandlesAsync(CandleSeries series, DateTime from, DateTime? to, CancellationToken cancellationToken);
}
