using Application.Dtos.Candles;
using Domain.Enums;

namespace Application.Interfaces;

/// <summary>Identifies one candle table. The table name is derived from it, never taken from the caller.</summary>
public readonly record struct CandleSeries(ExchangeEnum Exchange, MarketTypeEnum MarketType, SymbolEnum Symbol, TimeFrameEnum Timeframe);

/// <summary>
/// Read side of the candle store. A seam on purpose: a caching decorator (memory / Redis) can wrap it later
/// without touching the endpoints.
/// </summary>
public interface ICandleReader
{
    /// <summary>Candles with <c>from &lt;= open_time &lt;= to</c>, oldest first, one page of at most <paramref name="limit"/>.</summary>
    Task<CandlePage> GetRangeAsync(CandleSeries series, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken);

    /// <summary>The newest <paramref name="limit"/> candles up to <paramref name="upTo"/>, oldest first (indicator lookback).</summary>
    Task<IReadOnlyList<CandleResponse>> GetLatestAsync(CandleSeries series, int limit, DateTime? upTo, CancellationToken cancellationToken);
}
