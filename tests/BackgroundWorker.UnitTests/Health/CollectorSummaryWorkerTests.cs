using BackgroundWorker.Health;
using BackgroundWorker.UnitTests.CandleSeries;
using BackgroundWorker.UnitTests.TestSupport;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Health;

/// <summary>The one-line summary the collector writes now and then: what it stored, how fresh it is, whether the streams are up.</summary>
public class CollectorSummaryWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTime Noon = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    private sealed class FakeCandleRepository : ICandleRepository
    {
        public DateTime? Latest { get; set; }
        public bool Throw { get; set; }
        public int Calls { get; private set; }

        public Task<DateTimeOffset?> GetFirstOpenTimeAsync(string tableName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DateTimeOffset?> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken)
        {
            Calls++;
            if (Throw)
                throw new InvalidOperationException("secret-host unreachable");
            return Task.FromResult<DateTimeOffset?>(Latest is { } l ? new DateTimeOffset(l) : null);
        }

        public Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<int> BulkInsert(string tableName, IEnumerable<Candle> candles, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Candle>> GetPageAsync(string tableName, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Candle>> GetLatestAsync(string tableName, int limit, DateTime? upTo, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<CandleGap>> GetGapsAsync(string tableName, TimeSpan interval, DateTime? expectedStart, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Rig : IAsyncDisposable
    {
        public FakeTimeProvider Time { get; } = new(new DateTimeOffset(Noon.AddMinutes(5)));
        public FakeCandleRepository Repository { get; } = new() { Latest = Noon.AddHours(-1) };
        public CollectionStats Stats { get; } = new();
        public WorkerHealthState Health { get; } = new(TimeProvider.System);
        public ListLogger<CollectorSummaryWorker> Log { get; } = new();
        public CollectorSummaryWorker Worker { get; private set; } = null!;

        public static async Task<Rig> StartAsync(int summaryMinutes = 10, bool withSeries = true)
        {
            var rig = new Rig();
            var scopes = new ServiceCollection().AddSingleton<ICandleRepository>(rig.Repository).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            var series = withSeries
                ? await CandleSeriesTestSupport.LoadedProviderAsync(rig.Time, TestPlans.Binance(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H))
                : CandleSeriesTestSupport.NewProvider(new FakeCandleSeriesSource(), rig.Time);

            rig.Worker = new CollectorSummaryWorker(
                rig.Log, Options.Create(new HealthConfigurations { SummaryIntervalMinutes = summaryMinutes }), rig.Stats, series,
                new CandleFreshnessMonitor(scopes, series, rig.Time), rig.Health, rig.Time);

            await rig.Worker.StartAsync(Ct);
            return rig;
        }

        public async Task WaitForEntriesAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                lock (Log.Entries)
                    if (Log.Entries.Count >= count)
                        return;

                Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {count} log entries");
                await Task.Delay(5, Ct);
            }
        }

        /// <summary>
        /// Lets one interval pass and waits for the line. The worker creates its timer a moment after it is started (the host does not
        /// wait for it), so a clock moved too early finds no timer; moving it again until a line appears does not depend on that moment.
        /// </summary>
        public async Task AdvanceUntilEntriesAsync(int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                lock (Log.Entries)
                    if (Log.Entries.Count >= count)
                        return;

                Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for {count} log entries");
                Time.Advance(TimeSpan.FromMinutes(10));
                await Task.Delay(20, Ct);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Worker.StopAsync(CancellationToken.None);
            Worker.Dispose();
        }
    }

    [Fact]
    public async Task AfterEachIntervalItLogsWhatWasStoredAndStartsCountingAgain()
    {
        await using var rig = await Rig.StartAsync();
        rig.Stats.Stored(MarketDataTypeEnum.Candle, 12);
        rig.Stats.Stored(MarketDataTypeEnum.FundingRate, 1);
        rig.Stats.Stored(MarketDataTypeEnum.OpenInterest, 4);
        rig.Stats.Stored(MarketDataTypeEnum.Liquidation, 37);

        await rig.AdvanceUntilEntriesAsync(1);
        rig.Time.Advance(TimeSpan.FromMinutes(10));
        await rig.WaitForEntriesAsync(2);

        var (level, first) = rig.Log.Entries[0];
        Assert.Equal(LogLevel.Information, level);
        Assert.StartsWith("Last 10 min: stored 12 candles, 1 funding rates, 4 open interest, 37 liquidations.", first);
        Assert.StartsWith("Last 10 min: stored 0 candles, 0 funding rates, 0 open interest, 0 liquidations.", rig.Log.Entries[1].Message);
    }

    [Fact]
    public async Task ItSaysHowFreshTheNewestCandleIs()
    {
        await using var rig = await Rig.StartAsync();

        await rig.AdvanceUntilEntriesAsync(1);

        // At 12:15 the candle that must be stored is the one that opened at 11:00, and it is.
        Assert.Contains("1 candle series, 0 behind (at most 0 candles), newest candle 2026-01-01 11:00.", rig.Log.Entries[0].Message);
    }

    [Fact]
    public async Task ATableThatFellBehindIsCountedAndSaysHowFar()
    {
        await using var rig = await Rig.StartAsync();
        rig.Repository.Latest = Noon.AddHours(-4);   // 8:00 is the newest; 11:00 should be there

        await rig.AdvanceUntilEntriesAsync(1);

        Assert.Contains("1 behind (at most 3 candles), newest candle 2026-01-01 08:00.", rig.Log.Entries[0].Message);
    }

    [Fact]
    public async Task ItCountsTheStreamsThatAreUp()
    {
        await using var rig = await Rig.StartAsync();
        rig.Health.SetConnected(HealthComponents.CandleStreamFutures, true);
        rig.Health.SetConnected(HealthComponents.LiquidationStream, true);
        rig.Health.SetConnected(HealthComponents.FuturesWebSocket, false);   // down
        // The spot candle stream was never used: it has no state and is not counted at all.

        await rig.AdvanceUntilEntriesAsync(1);

        Assert.EndsWith("Streams up 2/3.", rig.Log.Entries[0].Message);
    }

    [Fact]
    public async Task ADatabaseThatDoesNotAnswerStillGetsASummaryWithoutTheFreshnessAndNeverStopsTheWorker()
    {
        await using var rig = await Rig.StartAsync();
        rig.Repository.Throw = true;
        rig.Stats.Stored(MarketDataTypeEnum.Candle, 5);

        await rig.AdvanceUntilEntriesAsync(1);

        var (level, message) = rig.Log.Entries[0];
        Assert.Equal(LogLevel.Information, level);
        Assert.Contains("stored 5 candles", message);
        Assert.Contains("freshness unknown (database not answering)", message);
        Assert.DoesNotContain("secret-host", message);   // the exception's text can carry host names

        rig.Repository.Throw = false;   // and it keeps going
        rig.Time.Advance(TimeSpan.FromMinutes(10));
        await rig.WaitForEntriesAsync(2);
        Assert.Contains("newest candle", rig.Log.Entries[1].Message);
    }

    [Fact]
    public async Task WithNoSeriesConfiguredItSaysSoAndDoesNotTouchTheDatabase()
    {
        await using var rig = await Rig.StartAsync(withSeries: false);

        await rig.AdvanceUntilEntriesAsync(1);

        Assert.Contains("No candle series configured yet.", rig.Log.Entries[0].Message);
        Assert.Equal(0, rig.Repository.Calls);
    }

    [Fact]
    public async Task ZeroMinutesTurnsTheSummaryOff()
    {
        await using var rig = await Rig.StartAsync(summaryMinutes: 0);

        rig.Time.Advance(TimeSpan.FromHours(5));
        await Task.Delay(100, Ct);

        Assert.Empty(rig.Log.Entries);
    }

    [Fact]
    public async Task NothingIsLoggedBeforeTheFirstIntervalHasPassed()
    {
        await using var rig = await Rig.StartAsync();

        rig.Time.Advance(TimeSpan.FromMinutes(9));
        await Task.Delay(100, Ct);

        Assert.Empty(rig.Log.Entries);
    }
}
