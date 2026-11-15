using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Exchange;

/// <summary>
/// Candles from an exchange's request/response API. Everything exchange-specific lives behind it: the symbol and interval
/// names, the page size, and the request budget (the exchange's rate limit is respected before each call).
/// </summary>
public interface ICandleGateway
{
    /// <summary>
    /// Whether this gateway can serve the candles of <paramref name="exchange"/>. The one the program runs on is a router over the gateways
    /// of every exchange it has; the collector only takes up the series of exchanges it can serve. The default (every exchange) is for a
    /// gateway of a single exchange that is only ever asked about its own.
    /// </summary>
    bool Serves(ExchangeEnum exchange) => true;

    /// <summary>
    /// One page of candles of <paramref name="series"/>, oldest first, whose open time is at or after <paramref name="from"/> (and at
    /// or before <paramref name="to"/>, inclusive, when given). The newest candle may still be open: deciding which are closed is
    /// the caller's. <see cref="ExchangePage{T}.HasMore"/> says whether to ask again from after the last one.
    /// </summary>
    Task<ExchangeResult<ExchangePage<Candle>>> GetCandlesAsync(CandleSeries series, DateTime from, DateTime? to, CancellationToken cancellationToken);
}
