using Application.Interfaces;
using Application.Queries.Candles;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace TradePulse.Benchmarks;

/// <summary>
/// Read latency of the candle API's query layer against a real Postgres holding a realistic amount of data.
/// <see cref="Reader"/> names the <see cref="ICandleReader"/> variant under test, so a caching decorator
/// is measured with the exact same scenarios by adding one entry to <see cref="CreateReader"/>.
/// </summary>
[Config(typeof(Config))]
public class CandleReadBenchmarks
{
    private sealed class Config : ManualConfig
    {
        public Config()
        {
            AddColumn(StatisticColumn.Median, StatisticColumn.P95);
            AddDiagnoser(BenchmarkDotNet.Diagnosers.MemoryDiagnoser.Default);
        }
    }

    // About 8.5 years of 15m candles, the size of the real futures table; 4h is the Ichimoku-style timeframe.
    private const int Rows15m = 300_000;
    private const int Rows4h = 18_750;
    private const int IchimokuLookback = 78; // 52 + 26

    private static readonly DateTime Start = new(2018, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly CandleSeries Series4h = new(ExchangeEnum.Binance, MarketTypeEnum.Futures, new ExchangeSymbol("BTCUSDT"), TimeFrameEnum._4H);
    private static readonly CandleSeries Series15m = new(ExchangeEnum.Binance, MarketTypeEnum.Futures, new ExchangeSymbol("BTCUSDT"), TimeFrameEnum._15M);

    private PostgreSqlContainer _container = null!;
    private RedisContainer? _redis;
    private ServiceProvider? _services;
    private ICandleReader _reader = null!;
    private DateTime[] _historicalPoints = null!;
    private int _next;

    /// <summary>
    /// Which reader to measure: <c>Postgres</c> (no cache), <c>Memory</c> (HybridCache, process memory only),
    /// <c>Redis</c> (HybridCache with the memory level off, so every hit is a Redis round trip).
    /// </summary>
    [Params("Postgres", "Memory", "Redis")]
    public string Reader { get; set; } = "";

    [GlobalSetup]
    public async Task Setup()
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        await _container.StartAsync();
        var connectionString = _container.GetConnectionString();

        var repository = new CandleRepository(connectionString);
        await Seed(repository, "binance_futures_btcusdt_15m", Rows15m, TimeSpan.FromMinutes(15));
        await Seed(repository, "binance_futures_btcusdt_4h", Rows4h, TimeSpan.FromHours(4));

        _reader = await CreateReader(Reader, repository);

        // Fixed spread of "as of" times across the history: the same sequence for every variant.
        var random = new Random(42);
        _historicalPoints = Enumerable.Range(0, 256)
            .Select(_ => Start.AddHours(4 * random.Next(IchimokuLookback, Rows4h - 1)))
            .ToArray();
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        if (_services is not null) await _services.DisposeAsync();
        if (_redis is not null) await _redis.DisposeAsync();
        await _container.DisposeAsync();
    }

    private async Task<ICandleReader> CreateReader(string name, CandleRepository repository)
    {
        var database = new CandleReader(repository);
        if (name == "Postgres")
            return database;

        var services = new ServiceCollection();
        if (name == "Redis")
        {
            _redis = new RedisBuilder("redis:7-alpine").Build();
            await _redis.StartAsync();
            services.AddStackExchangeRedisCache(options => options.Configuration = _redis.GetConnectionString());
        }
        else if (name != "Memory")
        {
            throw new ArgumentException($"Unknown reader '{name}'.");
        }

        services.AddHybridCache();
        services.Configure<CandleCacheOptions>(o => o.UseLocalCache = name == "Memory");
        _services = services.BuildServiceProvider();

        return new CachedCandleReader(
            database,
            _services.GetRequiredService<Microsoft.Extensions.Caching.Hybrid.HybridCache>(),
            _services.GetRequiredService<Microsoft.Extensions.Options.IOptions<CandleCacheOptions>>(),
            TimeProvider.System);
    }

    /// <summary>The same request every time: the "dashboard polls the newest window" case, best case for a cache.</summary>
    [Benchmark(Baseline = true, Description = "latest 78 x 4h (same request)")]
    public Task<IReadOnlyList<Application.Dtos.Candles.CandleResponse>> LatestWindowRepeated() =>
        _reader.GetLatestAsync(Series4h, IchimokuLookback, null, CancellationToken.None);

    /// <summary>256 different "as of" points cycled through: backtest-style access. Warm, so after warmup these are all hits.</summary>
    [Benchmark(Description = "latest 78 x 4h (256 'to' values)")]
    public Task<IReadOnlyList<Application.Dtos.Candles.CandleResponse>> LatestWindowHistorical() =>
        _reader.GetLatestAsync(Series4h, IchimokuLookback, _historicalPoints[_next++ & 255], CancellationToken.None);

    [Benchmark(Description = "latest 1 x 15m")]
    public Task<IReadOnlyList<Application.Dtos.Candles.CandleResponse>> LatestSingle15m() =>
        _reader.GetLatestAsync(Series15m, 1, null, CancellationToken.None);

    [Benchmark(Description = "range page 500 x 15m")]
    public Task<Application.Dtos.Candles.CandlePage> RangePage500() =>
        _reader.GetRangeAsync(Series15m, Start.AddDays(1000), null, 500, CancellationToken.None);

    private static async Task Seed(CandleRepository repository, string table, int rows, TimeSpan interval)
    {
        await repository.EnsureTableExistsAsync(table, CancellationToken.None);

        const int chunk = 50_000;
        for (var offset = 0; offset < rows; offset += chunk)
        {
            var candles = Enumerable.Range(offset, Math.Min(chunk, rows - offset)).Select(i =>
            {
                var open = Start + i * interval;
                var price = 20_000m + i % 5_000;
                return new Candle(open, price, price + 50m, price - 50m, price + 10m, 123.456m,
                    open + interval - TimeSpan.FromMilliseconds(1), price * 123.456m, 1_000 + i % 500, 60m, price * 60m);
            });
            await repository.BulkInsert(table, candles, CancellationToken.None);
        }
    }
}
