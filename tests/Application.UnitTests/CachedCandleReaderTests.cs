using Application.Dtos.Candles;
using Application.Interfaces;
using Application.Queries.Candles;
using Domain.Enums;
using Domain.Repositories;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Application.UnitTests;

public class CachedCandleReaderTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly CandleSeries Series = new(ExchangeEnum.Binance, MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class CountingReader : ICandleReader
    {
        public int Calls { get; private set; }
        public Exception? FailWith { get; set; }

        public Task<CandlePage> GetRangeAsync(CandleSeries series, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken)
        {
            Calls++;
            if (FailWith is not null) throw FailWith;
            return Task.FromResult(new CandlePage([Candle(from ?? Now)], NextFrom: (from ?? Now).AddHours(1)));
        }

        public Task<IReadOnlyList<CandleResponse>> GetLatestAsync(CandleSeries series, int limit, DateTime? upTo, CancellationToken cancellationToken)
        {
            Calls++;
            if (FailWith is not null) throw FailWith;
            return Task.FromResult<IReadOnlyList<CandleResponse>>([Candle(upTo ?? Now)]);
        }
    }

    // HybridCache ignores MemoryDistributedCache as a second level (it adds nothing over L1), so tests need their own.
    // Stores bytes, like Redis, so every hit goes through the serializer.
    private sealed class DictionaryDistributedCache : IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _store = [];

        public int Count { get { lock (_store) return _store.Count; } }

        public byte[]? Get(string key) { lock (_store) return _store.GetValueOrDefault(key); }
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => Task.FromResult(Get(key));
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) { lock (_store) _store[key] = value; }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key) { }
        public Task RefreshAsync(string key, CancellationToken token = default) => Task.CompletedTask;
        public void Remove(string key) { lock (_store) _store.Remove(key); }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }

    private static CandleResponse Candle(DateTime open) =>
        new(open, 1m, 4m, 0.5m, 3m, 10m, open.AddHours(1).AddMilliseconds(-1), 25m, 42, 6m, 15m);

    private static (CachedCandleReader Reader, CountingReader Inner, DictionaryDistributedCache? L2) Create(Action<CandleCacheOptions>? configure = null, bool distributed = false)
    {
        var services = new ServiceCollection();
        services.AddHybridCache();
        var l2 = distributed ? new DictionaryDistributedCache() : null;
        if (l2 is not null)
            services.AddSingleton<IDistributedCache>(l2);

        var options = new CandleCacheOptions();
        configure?.Invoke(options);

        var inner = new CountingReader();
        var time = new FakeTimeProvider(new DateTimeOffset(Now));
        var reader = new CachedCandleReader(
            inner, services.BuildServiceProvider().GetRequiredService<HybridCache>(), Options.Create(options), time);

        return (reader, inner, l2);
    }

    [Fact]
    public async Task RepeatedLatestRequestHitsTheInnerReaderOnce()
    {
        var (reader, inner, _) = Create();

        await reader.GetLatestAsync(Series, 78, null, Ct);
        await reader.GetLatestAsync(Series, 78, null, Ct);

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task DifferentParametersAreCachedSeparately()
    {
        var (reader, inner, _) = Create();

        await reader.GetLatestAsync(Series, 78, null, Ct);
        await reader.GetLatestAsync(Series, 52, null, Ct);
        await reader.GetLatestAsync(Series, 78, Now.AddDays(-3), Ct);
        await reader.GetLatestAsync(Series with { Timeframe = TimeFrameEnum._4H }, 78, null, Ct);
        await reader.GetLatestAsync(Series with { Symbol = SymbolEnum.ETHUSDT }, 78, null, Ct);

        Assert.Equal(5, inner.Calls);
    }

    [Fact]
    public async Task TheSameInstantInDifferentKindsIsOneEntry()
    {
        var (reader, inner, _) = Create();
        var utc = Now.AddDays(-3);

        await reader.GetLatestAsync(Series, 78, utc, Ct);
        await reader.GetLatestAsync(Series, 78, utc.ToLocalTime(), Ct);

        Assert.Equal(1, inner.Calls);
    }

    [Fact]
    public async Task RangeRequestsAreCachedAndKeyedByEveryArgument()
    {
        var (reader, inner, _) = Create();

        await reader.GetRangeAsync(Series, Now.AddDays(-9), Now.AddDays(-8), 500, Ct);
        await reader.GetRangeAsync(Series, Now.AddDays(-9), Now.AddDays(-8), 500, Ct);
        await reader.GetRangeAsync(Series, Now.AddDays(-9), Now.AddDays(-8), 100, Ct);
        await reader.GetRangeAsync(Series, Now.AddDays(-10), Now.AddDays(-8), 500, Ct);

        Assert.Equal(3, inner.Calls);
    }

    [Fact]
    public async Task ErrorsAreNotCached()
    {
        var (reader, inner, _) = Create();
        inner.FailWith = new CandleSeriesNotFoundException("binance_futures_btcusdt_1h");

        await Assert.ThrowsAsync<CandleSeriesNotFoundException>(async () => await reader.GetLatestAsync(Series, 78, null, Ct));

        inner.FailWith = null; // the collector created the table in the meantime
        var candles = await reader.GetLatestAsync(Series, 78, null, Ct);

        Assert.Single(candles);
        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task AnswersThatReachTheNewestCandleExpireQuicklyButSettledHistoryDoesNot()
    {
        var (reader, inner, _) = Create(o =>
        {
            o.LatestTtl = TimeSpan.FromMilliseconds(100);
            o.HistoricalTtl = TimeSpan.FromHours(1);
        });
        var settled = Now.AddDays(-30);

        await reader.GetLatestAsync(Series, 78, null, Ct);       // reaches the newest candle
        await reader.GetLatestAsync(Series, 78, settled, Ct);    // every candle closed long ago
        await Task.Delay(500, Ct);
        await reader.GetLatestAsync(Series, 78, null, Ct);
        await reader.GetLatestAsync(Series, 78, settled, Ct);

        Assert.Equal(3, inner.Calls); // newest refetched, settled served from cache
    }

    [Fact]
    public async Task ACandleThatJustClosedIsNotTreatedAsSettled()
    {
        var (reader, inner, _) = Create(o => o.LatestTtl = TimeSpan.FromMilliseconds(100));
        var justClosed = Now.AddHours(-1); // less than two intervals ago

        await reader.GetLatestAsync(Series, 78, justClosed, Ct);
        await Task.Delay(500, Ct);
        await reader.GetLatestAsync(Series, 78, justClosed, Ct);

        Assert.Equal(2, inner.Calls);
    }

    [Fact]
    public async Task DistributedLevelRoundTripsTheDataWithoutLosingAnything()
    {
        // Without the memory level every hit is deserialized from the distributed cache, as it would be from Redis.
        var (reader, inner, l2) = Create(o => o.UseLocalCache = false, distributed: true);
        var asOf = Now.AddDays(-5);

        var first = await reader.GetLatestAsync(Series, 78, asOf, Ct);
        await WaitForL2Write(l2!, expectedEntries: 1);
        var second = await reader.GetLatestAsync(Series, 78, asOf, Ct);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(first, second);
        Assert.Equal(DateTimeKind.Utc, second[0].OpenTime.Kind);

        var page1 = await reader.GetRangeAsync(Series, asOf, asOf.AddDays(1), 10, Ct);
        await WaitForL2Write(l2!, expectedEntries: 2);
        var page2 = await reader.GetRangeAsync(Series, asOf, asOf.AddDays(1), 10, Ct);
        Assert.Equal(2, inner.Calls);
        Assert.Equal(page1.Items, page2.Items);
        Assert.Equal(page1.NextFrom, page2.NextFrom);
    }

    // HybridCache may write the distributed copy of an immutable value in the background; wait for it to land.
    private static async Task WaitForL2Write(DictionaryDistributedCache l2, int expectedEntries)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (l2.Count < expectedEntries && DateTime.UtcNow < deadline)
            await Task.Delay(10, Ct);

        Assert.True(l2.Count >= expectedEntries, "the value never reached the distributed cache");
    }
}
