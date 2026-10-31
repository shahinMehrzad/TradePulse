using Application.Exchange;
using BackgroundWorker.Alerts;
using BackgroundWorker.Collection.Candles;
using BackgroundWorker.Health;
using BackgroundWorker.UnitTests.CandleSeries;
using Domain.Entities;
using Domain.Enums;
using Domain.Repositories;
using Domain.ValueObjects;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackgroundWorker.UnitTests.Collection;

/// <summary>
/// What the candle worker reports as still open after its gap repair: the real worker, a fake exchange and a fake candle table.
/// Only the repair at startup is looked at; the worker then waits for the next candle boundary and is stopped.
/// </summary>
public class CandleGapReportingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string Table = TestPlans.Binance(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H).Series.TableName;

    private static CandleGap Gap(int fromHour, int toHour) =>
        new(new DateTime(2021, 8, 13, fromHour, 0, 0, DateTimeKind.Utc), new DateTime(2021, 8, 13, toHour, 0, 0, DateTimeKind.Utc));

    private static Candle ClosedCandle(DateTime open) =>
        new(open, 1m, 2m, 0.5m, 1.5m, 10m, open.AddHours(1).AddMilliseconds(-1), 15m, 3, 5m, 7m);

    /// <summary>The table: up to date (so the normal sync has nothing to ask for), with the holes it is told to have.</summary>
    private sealed class FakeCandleTable(params IReadOnlyList<CandleGap>[] gapsPerLook) : ICandleRepository
    {
        private int _looks;
        public int Looks => Volatile.Read(ref _looks);

        public Task<IReadOnlyList<CandleGap>> GetGapsAsync(string tableName, TimeSpan interval, DateTime? expectedStart, CancellationToken cancellationToken)
        {
            var look = Interlocked.Increment(ref _looks) - 1;
            return Task.FromResult(gapsPerLook[Math.Min(look, gapsPerLook.Length - 1)]);
        }

        public Task<DateTimeOffset?> GetFirstOpenTimeAsync(string tableName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DateTimeOffset?> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken) =>
            Task.FromResult<DateTimeOffset?>(DateTimeOffset.UtcNow);   // nothing newer can be closed yet

        public Task<int> BulkInsert(string tableName, IEnumerable<Candle> candles, CancellationToken cancellationToken) => Task.FromResult(candles.Count());
        public Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Candle>> GetPageAsync(string tableName, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<Candle>> GetLatestAsync(string tableName, int limit, DateTime? upTo, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>An exchange that has the candles it is given for a gap's first period, and nothing else.</summary>
    private sealed class FakeCandleGateway(params DateTime[] candlesItHas) : ICandleGateway
    {
        public Task<ExchangeResult<ExchangePage<Candle>>> GetCandlesAsync(Domain.ValueObjects.CandleSeries series, DateTime from, DateTime? to, CancellationToken cancellationToken)
        {
            var items = candlesItHas.Where(o => o >= from && (to is null || o <= to)).Select(ClosedCandle).ToList();
            return Task.FromResult(ExchangeResult<ExchangePage<Candle>>.Ok(new ExchangePage<Candle>(items, HasMore: false)));
        }
    }

    private static async Task<OpenGaps> RunRepairAsync(FakeCandleTable table, FakeCandleGateway gateway)
    {
        var openGaps = new OpenGaps();
        var scopes = new ServiceCollection().AddSingleton<ICandleRepository>(table).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var provider = await CandleSeriesTestSupport.LoadedProviderAsync(TimeProvider.System, TestPlans.Binance(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H));
        using var worker = new BinanceCandleWorker(
            scopes, NullLogger<BinanceCandleWorker>.Instance, provider, gateway, new CandleSyncCoordinator(),
            new WorkerHealthState(TimeProvider.System), openGaps, new GapRepairSchedule(TimeProvider.System), new AlertQueue(), new CollectionStats());

        await worker.StartAsync(Ct);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (openGaps.Get(Table) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "the repair did not report");
            await Task.Delay(5, Ct);
        }

        await worker.StopAsync(CancellationToken.None);
        provider.Dispose();
        return openGaps;
    }

    [Fact]
    public async Task AGapTheExchangeHasNoCandlesForStaysOpenAndIsReported()
    {
        var table = new FakeCandleTable([Gap(2, 5)]);   // four hours missing, and the exchange has none of them

        var gaps = await RunRepairAsync(table, new FakeCandleGateway());

        Assert.Equal(new GapSummary(1, 4, new DateTime(2021, 8, 13, 2, 0, 0, DateTimeKind.Utc)), gaps.Get(Table));
        Assert.Equal(1, table.Looks);   // nothing was inserted, so there is nothing to look at again
    }

    [Fact]
    public async Task AGapTheExchangeFillsIsNotReportedAnymore()
    {
        var table = new FakeCandleTable([Gap(2, 3)], []);   // the second look finds the table complete
        var gateway = new FakeCandleGateway(Gap(2, 3).FirstMissingOpenTime, Gap(2, 3).LastMissingOpenTime);

        var gaps = await RunRepairAsync(table, gateway);

        Assert.Equal(new GapSummary(0, 0, null), gaps.Get(Table));
        Assert.Equal(2, table.Looks);
    }

    [Fact]
    public async Task AGapFilledOnlyInPartLeavesTheRestReportedNotTheWholeGap()
    {
        var table = new FakeCandleTable([Gap(2, 5)], [Gap(4, 5)]);                 // 02:00 and 03:00 came, 04:00 and 05:00 did not
        var gateway = new FakeCandleGateway(Gap(2, 5).FirstMissingOpenTime, Gap(2, 5).FirstMissingOpenTime.AddHours(1));

        var gaps = await RunRepairAsync(table, gateway);

        Assert.Equal(new GapSummary(1, 2, new DateTime(2021, 8, 13, 4, 0, 0, DateTimeKind.Utc)), gaps.Get(Table));
    }

    [Fact]
    public async Task ATableWithoutGapsIsReportedAsCheckedAndComplete()
    {
        var noGaps = (IReadOnlyList<CandleGap>)[];
        var gaps = await RunRepairAsync(new FakeCandleTable(noGaps), new FakeCandleGateway());

        Assert.Equal(new GapSummary(0, 0, null), gaps.Get(Table));
    }
}
