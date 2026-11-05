using Application.Queries.Dashboard;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.UnitTests;

public class GetMarketStatisticsQueryHandlerTests
{
    private const string LongShortTable = "binance_futures_btcusdt_1h_longshortratio";
    private const string TakerTable = "binance_futures_btcusdt_1h_takervolume";
    private static readonly DateTime Noon = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeReader<T> : ITimeSeriesReader<T>
    {
        public List<T> Rows { get; } = [];
        public int? AskedFor { get; private set; }

        public Task<IReadOnlyList<T>> GetNewestAsync(string tableName, int count, CancellationToken cancellationToken)
        {
            AskedFor = count;
            return Task.FromResult<IReadOnlyList<T>>([.. Rows.TakeLast(count)]);
        }
    }

    private readonly FakeCandleWorkerConfigurationRepository _configurations = new();
    private readonly FakeMarketDataRepository<LongShortRatio> _longShortTables = new();
    private readonly FakeMarketDataRepository<TakerVolume> _takerTables = new();
    private readonly FakeReader<LongShortRatio> _longShort = new();
    private readonly FakeReader<TakerVolume> _taker = new();

    private GetMarketStatisticsQueryHandler Handler => new(_configurations, _longShortTables, _longShort, _takerTables, _taker);

    private void Configure(TimeFrameEnum timeFrame, bool statistics) =>
        _configurations.Configurations.Add(new CandleWorkerConfiguration
        {
            SymbolListing = new SymbolListing
            {
                ExchangeId = (int)ExchangeEnum.Binance, MarketId = (int)MarketTypeEnum.Futures, SymbolId = (int)SeededSymbol.BTCUSDT, ExchangeSymbol = nameof(SeededSymbol.BTCUSDT),
            },
            TimeFrameId = (int)timeFrame,
            CollectMarketStatistics = statistics,
        });

    private static GetMarketStatisticsQuery Query(TimeFrameEnum timeFrame = TimeFrameEnum._1H, int? points = null) =>
        new(ExchangeEnum.Binance, MarketTypeEnum.Futures, "btcusdt", timeFrame, points);

    [Fact]
    public async Task ATimeFrameThatIsNotCollectedIsNotFound()
    {
        Configure(TimeFrameEnum._1H, statistics: true);

        await Assert.ThrowsAsync<ConfigurationNotFoundException>(() => Handler.HandleAsync(Query(TimeFrameEnum._4H), Ct));
    }

    [Fact]
    public async Task ASeriesThatDoesNotCollectThemAnswersNotCollectedAndReadsNothing()
    {
        Configure(TimeFrameEnum._1H, statistics: false);

        var answer = await Handler.HandleAsync(Query(), Ct);

        Assert.False(answer.Collected);
        Assert.Empty(answer.LongShort);
        Assert.Empty(answer.TakerVolume);
        Assert.Null(_longShort.AskedFor);
        Assert.Null(_taker.AskedFor);
    }

    [Fact]
    public async Task TablesThatDoNotExistYetAreEmptyListsNotAnError()
    {
        Configure(TimeFrameEnum._1H, statistics: true);

        var answer = await Handler.HandleAsync(Query(), Ct);

        Assert.True(answer.Collected);
        Assert.Empty(answer.LongShort);
        Assert.Empty(answer.TakerVolume);
        Assert.Null(_longShort.AskedFor);   // a missing table is never read (the real reader would throw)
    }

    [Fact]
    public async Task TheRowsOfBothTablesAreHandedOnWithTheirValues()
    {
        Configure(TimeFrameEnum._1H, statistics: true);
        _longShortTables.Latest[LongShortTable] = Noon;
        _takerTables.Latest[TakerTable] = Noon;
        _longShort.Rows.Add(new LongShortRatio(Noon.AddHours(-1), 1.5m, 0.6m, 0.4m));
        _longShort.Rows.Add(new LongShortRatio(Noon, 1.2m, 0.55m, 0.45m));
        _taker.Rows.Add(new TakerVolume(Noon, 0.8m, 40m, 50m));

        var answer = await Handler.HandleAsync(Query(), Ct);

        Assert.Equal(("1h", "btcusdt"), (answer.Timeframe, answer.Symbol));
        Assert.Equal([1.5m, 1.2m], answer.LongShort.Select(p => p.Ratio));
        Assert.Equal((0.55m, 0.45m), (answer.LongShort[1].LongAccount, answer.LongShort[1].ShortAccount));
        var taker = Assert.Single(answer.TakerVolume);
        Assert.Equal((Noon, 0.8m, 40m, 50m), (taker.Time, taker.BuySellRatio, taker.BuyVolume, taker.SellVolume));
    }

    [Theory]
    [InlineData(null, GetMarketStatisticsQueryHandler.DefaultPoints)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(80, 80)]
    [InlineData(100_000, GetMarketStatisticsQueryHandler.MaxPoints)]
    public async Task HowManyRowsAreReadIsKeptBetweenTwoAndTheMaximum(int? asked, int expected)
    {
        Configure(TimeFrameEnum._1H, statistics: true);
        _longShortTables.Latest[LongShortTable] = Noon;
        _takerTables.Latest[TakerTable] = Noon;

        await Handler.HandleAsync(Query(points: asked), Ct);

        Assert.Equal(expected, _longShort.AskedFor);
        Assert.Equal(expected, _taker.AskedFor);
    }
}
