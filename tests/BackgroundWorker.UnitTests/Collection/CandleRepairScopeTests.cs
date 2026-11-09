using System.Collections.Concurrent;
using Application.Exchange;
using Application.Notifications;
using BackgroundWorker.Alerts;
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

    private static readonly CandleSeriesPlan Btc1h = TestPlans.Binance(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H);
    private static readonly CandleSeriesPlan Eth1h = TestPlans.Binance(MarketTypeEnum.Futures, SeededSymbol.ETHUSDT, TimeFrameEnum._1H);
    private static readonly CandleSeriesPlan Btc4h = TestPlans.Binance(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._4H);

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

        public Func<string, IReadOnlyList<CandleGap>> RecentGapsFor { get; set; } = _ => [];
        public ConcurrentDictionary<string, int> RecentLooks { get; } = new();
        public ConcurrentDictionary<string, DateTime> RecentSince { get; } = new();

        public Task<IReadOnlyList<CandleGap>> GetRecentGapsAsync(string tableName, TimeSpan interval, DateTime since, CancellationToken cancellationToken)
        {
            Count(RecentLooks, tableName);
            RecentSince[tableName] = since;
            return Task.FromResult(RecentGapsFor(tableName));
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

    private sealed class RecordingAlerts : IAlertSink
    {
        private readonly List<Alert> _raised = [];

        public IReadOnlyList<Alert> Raised
        {
            get
            {
                lock (_raised)
                    return [.. _raised];
            }
        }

        public void Raise(Alert alert)
        {
            lock (_raised)
                _raised.Add(alert);
        }
    }

    /// <summary>An exchange that answers every request with an error.</summary>
    private sealed class FailingGateway : ICandleGateway
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task<ExchangeResult<ExchangePage<Candle>>> GetCandlesAsync(Domain.ValueObjects.CandleSeries series, DateTime from, DateTime? to, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(ExchangeResult<ExchangePage<Candle>>.Fail("down"));
        }
    }

    private sealed class Rig : IAsyncDisposable
    {
        public RecordingAlerts Alerts { get; } = new();
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        public FakeCandleSeriesSource Source { get; } = new();
        public CountingTable Table { get; } = new();
        public CountingGateway Gateway { get; init; } = new();
        public OpenGaps Gaps { get; } = new();
        public CandleSyncCoordinator Coordinator { get; } = new();
        public CandleSeriesProvider Provider { get; private set; } = null!;
        public GapRepairSchedule Schedule { get; private set; } = null!;
        public BinanceCandleWorker Worker => _worker;
        private BinanceCandleWorker _worker = null!;

        public ICandleGateway AnyGateway { get; private set; } = null!;

        public static Task<Rig> StartAsync(Func<string, IReadOnlyList<CandleGap>>? gapsFor, CountingGateway? gateway, params CandleSeriesPlan[] series) =>
            StartAsync(gapsFor, (ICandleGateway)(gateway ?? new CountingGateway()), series);

        public static async Task<Rig> StartAsync(Func<string, IReadOnlyList<CandleGap>>? gapsFor, ICandleGateway gateway, params CandleSeriesPlan[] series)
        {
            var rig = new Rig { AnyGateway = gateway, Gateway = gateway as CountingGateway ?? new CountingGateway() };
            if (gapsFor is not null)
                rig.Table.GapsFor = gapsFor;

            rig.Source.Series.AddRange(series);
            rig.Provider = CandleSeriesTestSupport.NewProvider(rig.Source, rig.Time);
            await rig.Provider.StartAsync(Ct);

            var scopes = new ServiceCollection().AddSingleton<ICandleRepository>(rig.Table).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            rig.Schedule = new GapRepairSchedule(rig.Time);
            rig._worker = new BinanceCandleWorker(
                scopes, NullLogger<BinanceCandleWorker>.Instance, rig.Provider, rig.AnyGateway, rig.Coordinator,
                new WorkerHealthState(TimeProvider.System), rig.Gaps, rig.Schedule, rig.Alerts, new CollectionStats())
            {
                Backoff = _ => TimeSpan.Zero,   // a request that keeps failing must not make a test wait a minute
            };
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

    // ---- the short repair of the newest candles ----

    private static readonly CandleGap RecentHole = new(Utc(2025, 12, 30, 3), Utc(2025, 12, 30, 5));

    /// <summary>The first pass has run, so the table has had its full repair and, with it, a look at the newest candles.</summary>
    private static Task WaitForTheFirstRepairAsync(Rig rig, CandleSeriesPlan plan) =>
        WaitUntilAsync(() => !rig.Schedule.IsRecentDue(TableOf(plan)), "the first repair");

    [Fact]
    public async Task TheShortRepairWaitsForItsHourAfterTheFullRepairHasLookedAtTheNewestCandles()
    {
        var gateway = new CountingGateway(Utc(2025, 12, 30, 3), Utc(2025, 12, 30, 4), Utc(2025, 12, 30, 5));
        await using var rig = await Rig.StartAsync(null, gateway, Btc1h);
        await WaitForTheFirstRepairAsync(rig, Btc1h);
        rig.Table.RecentGapsFor = _ => [RecentHole];

        await rig.Worker.RepairRecentGapsAsync([Btc1h], Ct);

        Assert.Equal((0, 0), (rig.Table.RecentLooks.GetValueOrDefault(TableOf(Btc1h)), gateway.Calls));
    }

    [Fact]
    public async Task ARecentHoleIsFilledWhenTheHourHasPassedWithoutWaitingForTheDailyRepair()
    {
        var gateway = new CountingGateway(Utc(2025, 12, 30, 3), Utc(2025, 12, 30, 4), Utc(2025, 12, 30, 5));
        await using var rig = await Rig.StartAsync(null, gateway, Btc1h);
        await WaitForTheFirstRepairAsync(rig, Btc1h);
        rig.Table.RecentGapsFor = _ => [RecentHole];

        rig.Time.Advance(TimeSpan.FromMinutes(61));
        await rig.Worker.RepairRecentGapsAsync([Btc1h], Ct);

        Assert.Equal(1, rig.Table.RecentLooks[TableOf(Btc1h)]);
        Assert.Equal(1, gateway.Calls);   // the one request for the range of the hole
        Assert.Equal(1, rig.Table.GapLooks[TableOf(Btc1h)]);   // the full scan was not repeated: it is still the daily one
    }

    [Fact]
    public async Task TheShortRepairLooksAtTheWindowAndNotBackToTheStartDate()
    {
        await using var rig = await Rig.StartAsync(null, null, Btc1h);
        await WaitForTheFirstRepairAsync(rig, Btc1h);

        rig.Time.Advance(TimeSpan.FromMinutes(61));
        await rig.Worker.RepairRecentGapsAsync([Btc1h], Ct);

        Assert.Equal(rig.Time.GetUtcNow().UtcDateTime - TimeSpan.FromDays(14), rig.Table.RecentSince[TableOf(Btc1h)]);
    }

    [Fact]
    public async Task AHoleTheExchangeDoesNotHaveIsAskedAboutThreeTimesAndThenLeftToTheDailyRepair()
    {
        var gateway = new CountingGateway();   // the exchange has nothing
        await using var rig = await Rig.StartAsync(null, gateway, Btc1h);
        await WaitForTheFirstRepairAsync(rig, Btc1h);
        rig.Table.RecentGapsFor = _ => [RecentHole];

        for (var hour = 1; hour <= 5; hour++)
        {
            rig.Time.Advance(TimeSpan.FromMinutes(61));
            await rig.Worker.RepairRecentGapsAsync([Btc1h], Ct);
        }

        Assert.Equal(5, rig.Table.RecentLooks[TableOf(Btc1h)]);   // looked at every hour
        Assert.Equal(3, gateway.Calls);                           // asked about only three times
    }

    [Fact]
    public async Task AFailedRequestIsNotAnAnswerSoTheHoleIsAskedAboutAgainEveryHour()
    {
        var gateway = new FailingGateway();
        await using var rig = await Rig.StartAsync(null, gateway, Btc1h);
        await WaitForTheFirstRepairAsync(rig, Btc1h);
        rig.Table.RecentGapsFor = _ => [RecentHole];

        var perPass = 0;
        for (var hour = 1; hour <= 5; hour++)
        {
            rig.Time.Advance(TimeSpan.FromMinutes(61));
            var before = gateway.Calls;
            await rig.Worker.RepairRecentGapsAsync([Btc1h], Ct);
            perPass = gateway.Calls - before;

            Assert.True(perPass > 0, $"pass {hour} did not ask the exchange: an error was counted as its answer");
        }

        Assert.Equal(0, rig.Schedule.AsksAbout(TableOf(Btc1h), RecentHole.FirstMissingOpenTime, RecentHole.LastMissingOpenTime));
    }

    [Fact]
    public async Task AHoleThatHasBeenFilledIsForgottenSoItsCountDoesNotStayBehind()
    {
        var gateway = new CountingGateway();   // nothing: the hole stays
        await using var rig = await Rig.StartAsync(null, gateway, Btc1h);
        await WaitForTheFirstRepairAsync(rig, Btc1h);
        rig.Table.RecentGapsFor = _ => [RecentHole];
        rig.Time.Advance(TimeSpan.FromMinutes(61));
        await rig.Worker.RepairRecentGapsAsync([Btc1h], Ct);
        Assert.Equal(1, rig.Schedule.AsksAbout(TableOf(Btc1h), RecentHole.FirstMissingOpenTime, RecentHole.LastMissingOpenTime));

        rig.Table.RecentGapsFor = _ => [];   // somebody filled it
        rig.Time.Advance(TimeSpan.FromMinutes(61));
        await rig.Worker.RepairRecentGapsAsync([Btc1h], Ct);

        Assert.Equal(0, rig.Schedule.AsksAbout(TableOf(Btc1h), RecentHole.FirstMissingOpenTime, RecentHole.LastMissingOpenTime));
    }

    [Fact]
    public async Task TheShortRepairLeavesWhatTheFullScanShowsAlone()
    {
        var inner = new CandleGap(Utc(2021, 8, 13, 2), Utc(2021, 8, 13, 5));
        await using var rig = await Rig.StartAsync(_ => [inner], null, Btc1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null, "the first repair");
        await WaitForTheFirstRepairAsync(rig, Btc1h);
        var shown = rig.Gaps.Get(TableOf(Btc1h));
        Assert.Equal(1, shown!.Value.Gaps);

        rig.Table.RecentGapsFor = _ => [];   // the last days are whole: that is no news about the old hole
        rig.Time.Advance(TimeSpan.FromMinutes(61));
        await rig.Worker.RepairRecentGapsAsync([Btc1h], Ct);

        Assert.Equal(shown, rig.Gaps.Get(TableOf(Btc1h)));
        Assert.Empty(rig.Alerts.Raised);
    }

    [Fact]
    public async Task AReconnectStillRepairsEveryTableAgain()
    {
        await using var rig = await Rig.StartAsync(null, null, Btc1h, Eth1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null && rig.Gaps.Get(TableOf(Eth1h)) is not null, "the first repair");

        rig.Coordinator.RequestResync();

        await WaitUntilAsync(() => rig.Table.GapLooks[TableOf(Btc1h)] >= 2 && rig.Table.GapLooks[TableOf(Eth1h)] >= 2, "both tables to be scanned again");
    }

    // ---- alerts: a hole that appears after the first look is news, the old ones of a new series are not ----

    private static readonly CandleGap InnerGap = new(Utc(2021, 8, 13, 2), Utc(2021, 8, 13, 5));

    [Fact]
    public async Task ANewHoleFoundByALaterLookIsAnnouncedOnceAndNotAgainWhileItStaysTheSame()
    {
        await using var rig = await Rig.StartAsync(null, null, Btc1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null, "the first look");

        rig.Table.GapsFor = _ => [InnerGap];
        rig.Coordinator.RequestResync();
        await WaitUntilAsync(() => rig.Alerts.Raised.Count == 1, "the alert");

        var alert = rig.Alerts.Raised[0];
        Assert.Equal($"New gap in {TableOf(Btc1h)}", alert.Title);
        Assert.Equal("1 gap(s), 4 candle(s) missing, from 2021-08-13 02:00 UTC. The exchange has no candles for them.", alert.Body);

        rig.Coordinator.RequestResync();
        await WaitUntilAsync(() => rig.Table.GapLooks[TableOf(Btc1h)] >= 3, "another look");
        Assert.Single(rig.Alerts.Raised);
    }

    [Fact]
    public async Task TheOldHolesOfASeriesAreNotNewsAtTheFirstLook()
    {
        await using var rig = await Rig.StartAsync(_ => [InnerGap], null, Btc1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null, "the first look");
        await Task.Delay(100, Ct);

        Assert.Empty(rig.Alerts.Raised);
    }

    [Fact]
    public async Task TheStartOfASeriesIsNeverAnnouncedAsAHole()
    {
        await using var rig = await Rig.StartAsync(_ => BeforeFirstCandle(), null, Btc1h);
        await WaitUntilAsync(() => rig.Gaps.Get(TableOf(Btc1h)) is not null, "the first look");
        rig.Coordinator.RequestResync();
        await WaitUntilAsync(() => rig.Table.GapLooks[TableOf(Btc1h)] >= 2, "a second look");

        Assert.Empty(rig.Alerts.Raised);
    }
}
