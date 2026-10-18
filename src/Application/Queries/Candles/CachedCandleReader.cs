using System.Globalization;
using Application.Dtos.Candles;
using Application.Interfaces;
using Application.Services;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;

namespace Application.Queries.Candles;

/// <summary>
/// Caching decorator over <see cref="ICandleReader"/>. <see cref="HybridCache"/> keeps entries in process memory (L1)
/// and, when a distributed cache is registered, in Redis (L2), and lets concurrent identical requests share one
/// database call. Errors (unknown series, bad input) come from the inner reader and are never cached.
/// </summary>
public sealed class CachedCandleReader(
    ICandleReader inner,
    HybridCache cache,
    IOptions<CandleCacheOptions> options,
    TimeProvider timeProvider) : ICandleReader
{
    private readonly CandleCacheOptions _options = options.Value;

    public async Task<CandlePage> GetRangeAsync(CandleSeries series, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken)
    {
        var key = $"candles:{Table(series)}:range:{Stamp(from)}:{Stamp(to)}:{limit}";

        return await cache.GetOrCreateAsync(
            key,
            (inner, series, from, to, limit),
            static async (state, ct) => await state.inner.GetRangeAsync(state.series, state.from, state.to, state.limit, ct),
            Entry(series, to),
            cancellationToken: cancellationToken);
    }

    public async Task<IReadOnlyList<CandleResponse>> GetLatestAsync(CandleSeries series, int limit, DateTime? upTo, CancellationToken cancellationToken)
    {
        var key = $"candles:{Table(series)}:latest:{limit}:{Stamp(upTo)}";

        var window = await cache.GetOrCreateAsync(
            key,
            (inner, series, limit, upTo),
            static async (state, ct) => new CandleWindow((await state.inner.GetLatestAsync(state.series, state.limit, state.upTo, ct)).ToArray()),
            Entry(series, upTo),
            cancellationToken: cancellationToken);

        return window.Items;
    }

    // Short-lived when the answer can still change (it reaches the newest candle), long-lived when every candle in it
    // closed long ago. "Long ago" is two intervals, so a candle that just closed is never treated as settled.
    private HybridCacheEntryOptions Entry(CandleSeries series, DateTime? end)
    {
        var settledBefore = timeProvider.GetUtcNow().UtcDateTime - 2 * TimeSpan.FromSeconds((int)series.Timeframe);
        var ttl = end is { } e && e.ToUniversalTime() <= settledBefore ? _options.HistoricalTtl : _options.LatestTtl;

        return new HybridCacheEntryOptions
        {
            Expiration = ttl,
            LocalCacheExpiration = ttl,
            Flags = _options.UseLocalCache ? HybridCacheEntryFlags.None : HybridCacheEntryFlags.DisableLocalCache
        };
    }

    private static string Table(CandleSeries s) => TableNameRegistry.GetCandleTableName(s.Exchange, s.MarketType, s.Symbol, s.Timeframe);

    // Ticks, not a formatted date: one canonical key per instant whatever DateTimeKind the caller used.
    private static string Stamp(DateTime? value) =>
        value is { } v ? v.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture) : "-";
}
