using Application.Dtos.Dashboard;
using Application.Queries.Candles;
using Application.Queries.Dashboard;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Microsoft.Extensions.Time.Testing;

namespace Application.UnitTests;

public class GetDashboardSeriesDetailQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTime NineOClock = Now.UtcDateTime.AddHours(-1);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeCandleWorkerConfigurationRepository _configurations = new();
    private readonly FakeSymbolDataConfigurationRepository _symbolData = new();
    private readonly FakeCandleRepository _candles = new();
    private readonly FakeMarketDataRepository<FundingRate> _funding = new();
    private readonly FakeMarketDataRepository<OpenInterest> _openInterest = new();
    private readonly FakeMarketDataRepository<Liquidation> _liquidation = new();
    private readonly FakeTimeProvider _time = new(Now);

    private GetDashboardSeriesDetailQueryHandler Handler => new(
        _configurations, _symbolData, _funding, _openInterest, _liquidation,
        new DashboardSnapshotBuilder(new CandleReader(_candles)), _time);

    private void Configure(MarketTypeEnum market, SeededSymbol symbol, TimeFrameEnum timeFrame, bool openInterest = false) =>
        _configurations.Configurations.Add(new CandleWorkerConfiguration
        {
            SymbolListing = new SymbolListing { ExchangeId = (int)ExchangeEnum.Binance, MarketId = (int)market, SymbolId = (int)symbol, ExchangeSymbol = symbol.ToString() },
            TimeFrameId = (int)timeFrame,
            CollectOpenInterest = openInterest
        });

    private void ConfigureSymbolData(bool funding, bool liquidation) =>
        _symbolData.Configurations.Add(new SymbolDataConfiguration
        {
            SymbolListing = new SymbolListing
            {
                ExchangeId = (int)ExchangeEnum.Binance, MarketId = (int)MarketTypeEnum.Futures, SymbolId = (int)SeededSymbol.BTCUSDT, ExchangeSymbol = nameof(SeededSymbol.BTCUSDT)
            },
            CollectFundingRate = funding, CollectLiquidation = liquidation
        });

    private void StoreCandles(string table, TimeSpan interval, decimal lastClose)
    {
        var lastOpen = Now.UtcDateTime - interval;
        _candles.PerTable[table] = [new Candle(lastOpen, lastClose - 1, lastClose + 1, lastClose - 2, lastClose, 5m, lastOpen + interval - TimeSpan.FromMilliseconds(1), 0m, 1, 0m, 0m)];
    }

    private static GetDashboardSeriesDetailQuery Query(MarketTypeEnum market = MarketTypeEnum.Futures, TimeFrameEnum? timeFrame = null) =>
        new(ExchangeEnum.Binance, market, "btcusdt", timeFrame);

    [Fact]
    public async Task ASymbolThatIsNotCollectedIsNotFound()
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.ETHUSDT, TimeFrameEnum._1H);

        await Assert.ThrowsAsync<ConfigurationNotFoundException>(() => Handler.HandleAsync(Query(), Ct));
    }

    [Fact]
    public async Task AMarketThatIsNotCollectedIsNotFound()
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H);

        await Assert.ThrowsAsync<ConfigurationNotFoundException>(() => Handler.HandleAsync(Query(MarketTypeEnum.Spot), Ct));
    }

    [Fact]
    public async Task ATimeFrameThatIsNotCollectedForTheSymbolIsNotFound()
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H);

        var error = await Assert.ThrowsAsync<ConfigurationNotFoundException>(() => Handler.HandleAsync(Query(timeFrame: TimeFrameEnum._4H), Ct));

        Assert.Contains("4h", error.Message);
    }

    [Fact]
    public async Task WithoutATimeFrameTheShortestCollectedOneIsSummarised()
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._4H);
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H);
        StoreCandles("binance_futures_btcusdt_1h", TimeSpan.FromHours(1), 100m);

        var detail = await Handler.HandleAsync(Query(), Ct);

        Assert.Equal(["1h", "4h"], detail.Timeframes);
        Assert.Equal("1h", detail.DefaultTimeframe);
        Assert.Equal("1h", detail.Current.Timeframe);
        Assert.Equal(100m, detail.Current.LastClose);
    }

    [Fact]
    public async Task AChosenTimeFrameIsSummarisedWhileTheDefaultStaysTheShortest()
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H);
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._4H);
        StoreCandles("binance_futures_btcusdt_1h", TimeSpan.FromHours(1), 100m);
        StoreCandles("binance_futures_btcusdt_4h", TimeSpan.FromHours(4), 200m);

        var detail = await Handler.HandleAsync(Query(timeFrame: TimeFrameEnum._4H), Ct);

        Assert.Equal("4h", detail.Current.Timeframe);
        Assert.Equal(200m, detail.Current.LastClose);
        Assert.Equal("1h", detail.DefaultTimeframe);
        Assert.Equal("binance", detail.Exchange);
        Assert.Equal(("futures", "btcusdt"), (detail.Market, detail.Symbol));
    }

    [Fact]
    public async Task TheNumberOfCandlesTheSummaryCoversCanBeChosen()
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H);
        _candles.PerTable["binance_futures_btcusdt_1h"] = [.. Enumerable.Range(0, 30).Select(i =>
        {
            var open = NineOClock - TimeSpan.FromHours(29 - i);
            return new Candle(open, 100m + i, 110m + i, 90m + i, 100m + i, 1m, open.AddHours(1).AddMilliseconds(-1), 0m, 1, 0m, 0m);
        })];
        var query = new GetDashboardSeriesDetailQuery(ExchangeEnum.Binance, MarketTypeEnum.Futures, "btcusdt", null, Candles: 5);

        var current = (await Handler.HandleAsync(query, Ct)).Current;
        var byDefault = (await Handler.HandleAsync(Query(), Ct)).Current;

        Assert.Equal(5, current.Sparkline!.Count);
        Assert.Equal(NineOClock.AddHours(-4), current.WindowStart);
        Assert.Equal(Now.UtcDateTime, current.WindowEnd);
        Assert.Equal(5m, current.Volume);
        Assert.Equal(DashboardSnapshotBuilder.SparklinePoints, byDefault.Sparkline!.Count); // without it, the same 24 as the cards
    }

    [Fact]
    public async Task SpotHasNoRelatedData()
    {
        Configure(MarketTypeEnum.Spot, SeededSymbol.BTCUSDT, TimeFrameEnum._1H);

        var detail = await Handler.HandleAsync(Query(MarketTypeEnum.Spot), Ct);

        Assert.Empty(detail.Related);
    }

    [Fact]
    public async Task OnlyTheDataTheAdminChoseToCollectIsListed()
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H);

        Assert.Empty((await Handler.HandleAsync(Query(), Ct)).Related);

        ConfigureSymbolData(funding: true, liquidation: false);
        var detail = await Handler.HandleAsync(Query(), Ct);

        Assert.Equal("fundingrate", Assert.Single(detail.Related).DataType);
    }

    [Fact]
    public async Task RelatedDataWhoseTableDoesNotExistYetIsEmpty()
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H, openInterest: true);
        ConfigureSymbolData(funding: true, liquidation: true);

        var detail = await Handler.HandleAsync(Query(), Ct);

        Assert.Equal(["fundingrate", "openinterest", "liquidation"], detail.Related.Select(r => r.DataType));
        Assert.All(detail.Related, r =>
        {
            Assert.Equal(DashboardStatus.Empty, r.Status);
            Assert.Null(r.LagSeconds);
            Assert.Null(r.LastTime);
        });
    }

    [Fact]
    public async Task ATableWithoutRowsIsEmptyToo()
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H);
        ConfigureSymbolData(funding: true, liquidation: false);
        _funding.Latest["binance_futures_btcusdt_fundingrate"] = null;

        var related = Assert.Single((await Handler.HandleAsync(Query(), Ct)).Related);

        Assert.Equal(DashboardStatus.Empty, related.Status);
    }

    [Theory]
    [InlineData(8 * 3600 + 60, DashboardStatus.Ok)]    // a whole funding interval plus exactly the grace
    [InlineData(8 * 3600 + 61, DashboardStatus.Late)]
    public async Task FundingRateIsLateOnceASettlementIsOverdue(int lagSeconds, string expected)
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H);
        ConfigureSymbolData(funding: true, liquidation: false);
        _funding.Latest["binance_futures_btcusdt_fundingrate"] = Now.UtcDateTime.AddSeconds(-lagSeconds);

        var related = Assert.Single((await Handler.HandleAsync(Query(), Ct)).Related);

        Assert.Equal(expected, related.Status);
        Assert.Equal(lagSeconds, related.LagSeconds);
        Assert.Equal(Now.UtcDateTime.AddSeconds(-lagSeconds), related.LastTime);
    }

    [Fact]
    public async Task OpenInterestIsJudgedAgainstTheChosenTimeFrameAndItsTable()
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H, openInterest: false);
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._4H, openInterest: true);
        _openInterest.Latest["binance_futures_btcusdt_4h_openinterest"] = Now.UtcDateTime.AddHours(-3);

        var onHour = await Handler.HandleAsync(Query(timeFrame: TimeFrameEnum._1H), Ct);
        var onFourHours = await Handler.HandleAsync(Query(timeFrame: TimeFrameEnum._4H), Ct);

        Assert.Empty(onHour.Related);                                    // not collected for 1h
        var related = Assert.Single(onFourHours.Related);
        Assert.Equal(("openinterest", DashboardStatus.Ok), (related.DataType, related.Status)); // 3 h behind a 4 h period is fine
    }

    [Fact]
    public async Task OpenInterestOlderThanItsPeriodIsLate()
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H, openInterest: true);
        _openInterest.Latest["binance_futures_btcusdt_1h_openinterest"] = Now.UtcDateTime.AddHours(-2);

        var related = Assert.Single((await Handler.HandleAsync(Query(), Ct)).Related);

        Assert.Equal(DashboardStatus.Late, related.Status);
    }

    [Fact]
    public async Task LiquidationsAreNeverLateHoweverOldTheLastOneIs()
    {
        Configure(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H);
        ConfigureSymbolData(funding: false, liquidation: true);
        _liquidation.Latest["binance_futures_btcusdt_liquidation"] = Now.UtcDateTime.AddDays(-3);

        var related = Assert.Single((await Handler.HandleAsync(Query(), Ct)).Related);

        Assert.Equal(DashboardStatus.Ok, related.Status);
        Assert.Equal(3 * 24 * 3600, related.LagSeconds);
    }
}
