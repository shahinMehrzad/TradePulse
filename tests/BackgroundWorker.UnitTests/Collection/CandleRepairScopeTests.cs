using System.Collections.Concurrent;
using Application.Exchange;
using BackgroundWorker.CandleSeries;
using BackgroundWorker.Collection.Candles;
using BackgroundWorker.Health;
using BackgroundWorker.UnitTests.CandleSeries;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Collection;

/// <summary>
/// How far the candle worker's work reaches: a symbol listed after the start date is a start and not a hole, and a change of the
/// series restarts only the loops it touches and scans only the tables that have not been scanned yet.
/// </summary>
public class CandleRepairScopeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CandleSeriesPlan Btc1h = TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);
    private static readonly CandleSeriesPlan Eth1h = TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.ETHUSDT, TimeFrameEnum._1H);
    private static readonly CandleSeriesPlan Btc4h = TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._4H);

    private static DateTime Utc(int year, int month, int day, int hour = 0) => new(year, month, day, hour, 0, 0, DateTimeKind.Utc);

    private sealed class CountingTable : ICandleRepository
    {
        public Func<string, IReadOnlyList<CandleGap>> GapsFor { get; set; } = _ => [];
        public ConcurrentDictionary<string, int> GapLooks { get; } = new();
        public ConcurrentDictionary<string, int> Syncs { get; } = new();
        public ConcurrentDictionary<string, int> Ensures { get; } = new();

        private static void Count(ConcurrentDictionary<string, int> counter, string table) => counter.AddOrUpdate(table, 1, (_, n) => n + 1);

        public Task<IReadOnlyList<CandleGap>> GetGapsAsync(string tableName, TimeSpan interval, DateTime? expectedStart, CancellationToken cancellationToken)
        {
            Count(GapLooks, tableName);
            return Task.FromResult(GapsFor(tableName));
        }

        public Task<DateTimeOffset?> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken)
        {
            Count(Syncs, tableName);
            return Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);   // nothing newer can be closed yet
        }

        public Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken)
        {
            Count(Ensures, tableName);
            return Task.CompletedTask;
        }

        public Task<int> BulkInsert(string tableName, IEnumerable<Candle> candles, CancellationToken cancellationToken) => Task.FromResult(candles.Count());
        public Task<DateTimeOffset?> GetFirstOpenTimeAsync(string tableName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Candle>> GetPageAsync(string tableName, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Candle>> GetLatestAsync(string tableName, int limit, DateTime? upTo, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>An exchange that has the candles it is told to have (open times), and counts how often it was asked.</summary>
    private sealed class CountingGateway(params DateTime[] candlesItHas) : ICandleGateway
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<ExchangeResult<ExchangePage<Candle>>> GetCandlesAsync(Domain.ValueObjects.CandleSeries series, DateTime from, DateTime? to, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            var items = candlesItHas.Where(o => o >= from && (to is null || o <= to))
                .Select(open => new Candle(open, 1m, 2m, 0.5m, 1.5m, 10m, open.AddHours(1).AddMilliseconds(-1), 15m, 3, 5m, 7m)).ToList();
            return Task.FromResult(ExchangeResult<ExchangePage<Candle>>.Ok(new ExchangePage<Candle>(items, HasMore: false)));
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        public FakeCandleSeriesSource Source { get; } = new();
        public CountingTable Table { get; } = new();
        public CountingGateway Gateway { get; init; } = new();
        public OpenGaps Gaps { get; } = new();
        public CandleSyncCoordinator Coordinator { get; } = new();
        public CandleSeriesProvider Provider { get; private set; } = null!;
        private BinanceCandleWorker _worker = null!;

        public static async Task<Rig> StartAsync(Func<string, IReadOnlyList<CandleGap>>? gapsFor, CountingGateway? gateway, params CandleSeriesPlan[] series)
        {
            var rig = new Rig { Gateway = gateway ?? new CountingGateway() };
            if (gapsFor is not null)
                rig.Table.GapsFor = gapsFor;

            rig.Source.Series.AddRange(series);
            rig.Provider = CandleSeriesTestSupport.NewProvider(rig.Source, rig.Time);
            await rig.Provider.StartAsync(Ct);

            var scopes = new ServiceCollection().AddSingleton<ICandleRepository>(rig.Table).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            rig._worker = new BinanceCandleWorker(
                scopes, NullLogger<BinanceCandleWorker>.Instance, rig.Provider, rig.Gateway, rig.Coordinator,
                new WorkerHealthState(TimeProvider.System), rig.Gaps, new GapRepairSchedule(rig.Time), new CollectionStats());
            await rig._worker.StartAsync(Ct);
            return rig;
        }

        /// <summary>The provider sets its status before it waits for the next look, so one jump of the clock can be lost: keep moving it.</summary>
        public Task ChangeTheChoiceAsync(Action change, long toVersion)
        {
            change();
            return WaitUntilAsync(() =>
            {
                Time.Advance(CandleSeriesProvider.RefreshInterval);
                return Provider.Version >= toVersion;
            }, $"version {toVersion}");
        }

        public async ValueTask DisposeAsync()
        {
            await _worker.StopAsync(CancellationToken.None);
            _worker.Dispose();
            await Provider.StopAsync(CancellationToken.None);
            Provider.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(5, Ct);
        }
    }

    private static string TableOf(CandleSeriesPlan plan) => plan.Series.TableName;

    // ---- a symbol listed after the start date ----

    // 1h series whose data begins on 6 January although it was asked for from 1 January
    private static IReadOnlyList<CandleGap> BeforeFirstCandle() =>
        [new CandleGap(Utc(2020, 1, 1), Utc(2020, 1, 5, 23), BeforeFirstCandle: true)];

    [Fact]
    public async Task TheRunBeforeTheFirstCandleIsTheStartOfTheSeriesNotAHole()
    {
        await using var rig = await Rig.StartAsync(_ => BeforeFirstCandle(), null, Btc1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null, "the repair");

        Assert.Equal(new GapSummary(0, 0, null, DataStartsAt: Utc(2020, 1, 6)), rig.Gaps.Get(TableOf(Btc1h)));
    }

    [Fact]
    public async Task TheExchangeIsAskedOnceAboutTheStartAndNotAgainAfterAReconnect()
    {
        await using var rig = await Rig.StartAsync(_ => BeforeFirstCandle(), null, Btc1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null, "the first repair");
        Assert.Equal(1, rig.Gateway.Calls);

        rig.Coordinator.RequestResync();   // a reconnect repairs every table again
        await WaitUntilAsync(() => rig.Table.GapLooks[TableOf(Btc1h)] >= 2, "the second repair");

        Assert.Equal(1, rig.Gateway.Calls);
    }

    [Fact]
    public async Task AHoleInsideTheSeriesIsStillReportedNextToItsStart()
    {
        var inner = new CandleGap(Utc(2021, 8, 13, 2), Utc(2021, 8, 13, 5));
        await using var rig = await Rig.StartAsync(_ => [BeforeFirstCandle()[0], inner], null, Btc1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null, "the repair");

        Assert.Equal(new GapSummary(1, 4, Utc(2021, 8, 13, 2), DataStartsAt: Utc(2020, 1, 6)), rig.Gaps.Get(TableOf(Btc1h)));
    }

    [Fact]
    public async Task IfTheExchangeDoesHaveTheEarlierCandlesTheyAreStoredAndThereIsNoLateStart()
    {
        var gateway = new CountingGateway(Utc(2020, 1, 1), Utc(2020, 1, 2));
        var calls = 0;
        await using var rig = await Rig.StartAsync(_ => Interlocked.Increment(ref calls) == 1 ? BeforeFirstCandle() : [], gateway, Btc1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null, "the repair");

        Assert.Equal(new GapSummary(0, 0, null), rig.Gaps.Get(TableOf(Btc1h)));
    }

    // ---- a change of the series touches only what it concerns ----

    [Fact]
    public async Task ASeriesAddedToARunningTimeFrameIsScannedButTheTablesAlreadyScannedAreNot()
    {
        await using var rig = await Rig.StartAsync(null, null, Btc1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null, "the first repair");

        await rig.ChangeTheChoiceAsync(() => rig.Source.Series.Add(Eth1h), toVersion: 2);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Eth1h)) is not null, "the new series to be scanned");

        Assert.Equal(1, rig.Table.GapLooks[TableOf(Btc1h)]);   // the loop restarted, the table was not scanned again
        Assert.Equal(1, rig.Table.GapLooks[TableOf(Eth1h)]);
    }

    [Fact]
    public async Task ASeriesInAnotherTimeFrameLeavesTheRunningLoopAlone()
    {
        await using var rig = await Rig.StartAsync(null, null, Btc1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null, "the first repair");

        await rig.ChangeTheChoiceAsync(() => rig.Source.Series.Add(Btc4h), toVersion: 2);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc4h)) is not null, "the new loop");

        Assert.Equal(1, rig.Table.Syncs[TableOf(Btc1h)]);      // the 1h loop did not even run its first pass again
        Assert.Equal(1, rig.Table.Ensures[TableOf(Btc1h)]);
    }

    [Fact]
    public async Task SwitchingOpenInterestOnForASeriesIsNoConcernOfTheCandleLoops()
    {
        await using var rig = await Rig.StartAsync(null, null, Btc1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null, "the first repair");

        await rig.ChangeTheChoiceAsync(() => rig.Source.Series[0] = Btc1h with { CollectOpenInterest = true }, toVersion: 2);
        await Task.Delay(150, Ct);

        Assert.Equal((1, 1), (rig.Table.Syncs[TableOf(Btc1h)], rig.Table.Ensures[TableOf(Btc1h)]));
    }

    [Fact]
    public async Task AReconnectStillRepairsEveryTableAgain()
    {
        await using var rig = await Rig.StartAsync(null, null, Btc1h, Eth1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null && rig.Gaps.Get(TableOf(Eth1h)) is not null, "the first repair");

        rig.Coordinator.RequestResync();

        await WaitUntilAsync(() => rig.Table.GapLooks[TableOf(Btc1h)] >= 2 && rig.Table.GapLooks[TableOf(Eth1h)] >= 2, "both tables to be scanned again");
    }
}
