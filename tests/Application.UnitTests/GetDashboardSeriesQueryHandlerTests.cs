using Application.Dtos.Dashboard;
using Application.Queries.Candles;
using Application.Queries.Dashboard;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Time.Testing;

namespace Application.UnitTests;

public class GetDashboardSeriesQueryHandlerTests
{
    // 10:00 UTC: the 09:00 hourly candle closed exactly now.
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeCandleWorkerConfigurationRepository _configurations = new();
    private readonly FakeCandleRepository _candles = new();
    private readonly FakeTimeProvider _time = new(Now);

    private GetDashboardSeriesQueryHandler Handler => new(_configurations, new DashboardSnapshotBuilder(new CandleReader(_candles)), _time);

    private void Configure(MarketTypeEnum market, SymbolEnum symbol, TimeFrameEnum timeFrame) =>
        _configurations.Configurations.Add(new CandleWorkerConfiguration
        {
            SymbolListing = new SymbolListing { ExchangeId = (int)ExchangeEnum.Binance, MarketId = (int)market, SymbolId = (int)symbol, ExchangeSymbol = symbol.ToString() },
            TimeFrameId = (int)timeFrame
        });

    /// <summary>Hourly candles ending with the one that opened at <paramref name="lastOpen"/>; close = 100 + index, so the change is easy to predict.</summary>
    private void Store(string table, int count, DateTime lastOpen, TimeSpan interval)
    {
        _candles.PerTable[table] = [.. Enumerable.Range(0, count).Select(i =>
        {
            var open = lastOpen - interval * (count - 1 - i);
            var close = 100m + i;
            return new Candle(open, close - 1, close + 2, close - 3, close, 10m, open + interval - TimeSpan.FromMilliseconds(1), 0m, 1, 0m, 0m);
        })];
    }

    private static readonly DateTime NineOClock = Now.UtcDateTime.AddHours(-1);

    [Fact]
    public async Task OneCardPerSymbolWithEveryTimeFrameAndTheShortestAsDefault()
    {
        Configure(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._4H);
        Configure(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);
        Configure(MarketTypeEnum.Spot, SymbolEnum.BTCUSDT, TimeFrameEnum._1D);

        var response = await Handler.HandleAsync(new(null), Ct);

        Assert.Equal(2, response.Items.Count);
        var spot = response.Items[0]; // markets are ordered by their enum value: spot first
        Assert.Equal(("binance", "spot", "btcusdt", "1d"), (spot.Exchange, spot.Market, spot.Symbol, spot.DefaultTimeframe));
        var futures = response.Items[1];
        Assert.Equal(["1h", "4h"], futures.Timeframes);
        Assert.Equal("1h", futures.DefaultTimeframe);
        Assert.Equal("1h", futures.Current.Timeframe);
    }

    [Fact]
    public async Task TheMarketFilterKeepsOnlyThatMarket()
    {
        Configure(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);
        Configure(MarketTypeEnum.Spot, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);

        var response = await Handler.HandleAsync(new(MarketTypeEnum.Spot), Ct);

        Assert.Equal("spot", Assert.Single(response.Items).Market);
    }

    [Fact]
    public async Task ASeriesWhoseTableDoesNotExistYetIsEmpty()
    {
        Configure(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);
        _candles.PerTable["some_other_table"] = [];

        var current = Assert.Single((await Handler.HandleAsync(new(null), Ct)).Items).Current;

        Assert.Equal(DashboardStatus.Empty, current.Status);
        Assert.Equal("1h", current.Timeframe);
        Assert.Null(current.LastClose);
        Assert.Null(current.Sparkline);
    }

    [Fact]
    public async Task ATableWithoutRowsIsEmpty()
    {
        Configure(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);
        _candles.PerTable["binance_futures_btcusdt_1h"] = [];

        var current = Assert.Single((await Handler.HandleAsync(new(null), Ct)).Items).Current;

        Assert.Equal(DashboardStatus.Empty, current.Status);
    }

    [Fact]
    public async Task ACandleThatJustClosedIsOkWithNoLag()
    {
        Configure(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);
        Store("binance_futures_btcusdt_1h", 30, NineOClock, TimeSpan.FromHours(1));

        var current = Assert.Single((await Handler.HandleAsync(new(null), Ct)).Items).Current;

        Assert.Equal(DashboardStatus.Ok, current.Status);
        Assert.Equal(0, current.LagSeconds);
        Assert.Equal(NineOClock, current.LastOpenTime);
    }

    [Fact]
    public async Task LagCountsFromTheCloseOfTheNewestCandle()
    {
        Configure(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);
        Store("binance_futures_btcusdt_1h", 30, NineOClock, TimeSpan.FromHours(1));
        _time.Advance(TimeSpan.FromMinutes(4));

        var current = Assert.Single((await Handler.HandleAsync(new(null), Ct)).Items).Current;

        Assert.Equal(240, current.LagSeconds);
        Assert.Equal(DashboardStatus.Ok, current.Status);
    }

    [Theory]
    [InlineData(3600 + 60, DashboardStatus.Ok)]   // a whole interval plus exactly the grace: the next candle is not overdue yet
    [InlineData(3600 + 61, DashboardStatus.Late)] // one second past it
    public async Task ItIsLateOnceTheNextCandleIsOverdue(int lagSeconds, string expected)
    {
        Configure(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);
        Store("binance_futures_btcusdt_1h", 30, NineOClock, TimeSpan.FromHours(1));
        _time.Advance(TimeSpan.FromSeconds(lagSeconds));

        var current = Assert.Single((await Handler.HandleAsync(new(null), Ct)).Items).Current;

        Assert.Equal(lagSeconds, current.LagSeconds);
        Assert.Equal(expected, current.Status);
    }

    [Fact]
    public async Task EverythingOnACardDescribesTheSame24NewestCandles()
    {
        Configure(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._15M);
        Store("binance_futures_btcusdt_15m", 100, Now.UtcDateTime.AddMinutes(-15), TimeSpan.FromMinutes(15)); // closes 100 .. 199

        var current = Assert.Single((await Handler.HandleAsync(new(null), Ct)).Items).Current;

        Assert.Equal(199m, current.LastClose);
        Assert.Equal(24, current.Sparkline!.Count);
        Assert.Equal(176m, current.Sparkline[0]);                                         // closes 176 .. 199
        Assert.Equal(199m, current.Sparkline[^1]);
        Assert.Equal(Math.Round((199m / 176m - 1m) * 100m, 2), current.ChangePercent);    // first point of the line to its last
        Assert.Equal(201m, current.High);                                                 // newest candle: close + 2
        Assert.Equal(173m, current.Low);                                                  // oldest of the 24 (close 176): close - 3
        Assert.Equal(240m, current.Volume);                                               // 24 candles x 10
        Assert.Equal(Now.UtcDateTime.AddHours(-6), current.WindowStart);                  // 24 candles of 15 minutes: the first one opened 6 h ago
        Assert.Equal(Now.UtcDateTime, current.WindowEnd);                                 // the newest closed just now
    }

    [Theory]
    [InlineData(true)]   // rising line
    [InlineData(false)]  // falling line
    public async Task TheChangeHasTheDirectionOfTheLine(bool rising)
    {
        Configure(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);
        var lastOpen = NineOClock;
        _candles.PerTable["binance_futures_btcusdt_1h"] = [.. Enumerable.Range(0, 30).Select(i =>
        {
            var open = lastOpen - TimeSpan.FromHours(29 - i);
            var close = rising ? 100m + i : 200m - i; // 30 candles; only the newest 24 are shown
            return new Candle(open, close, close + 1, close - 1, close, 1m, open.AddHours(1).AddMilliseconds(-1), 0m, 1, 0m, 0m);
        })];

        var current = Assert.Single((await Handler.HandleAsync(new(null), Ct)).Items).Current;

        Assert.Equal(rising, current.ChangePercent > 0);
        Assert.Equal(rising, current.Sparkline![^1] > current.Sparkline[0]);
    }

    [Fact]
    public async Task ANewSeriesWithFewerCandlesThanThatShowsWhatItHas()
    {
        Configure(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);
        Store("binance_futures_btcusdt_1h", 3, NineOClock, TimeSpan.FromHours(1)); // closes 100 .. 102

        var current = Assert.Single((await Handler.HandleAsync(new(null), Ct)).Items).Current;

        Assert.Equal(Math.Round((102m / 100m - 1m) * 100m, 2), current.ChangePercent);
        Assert.Equal(3, current.Sparkline!.Count);
        Assert.Equal(NineOClock.AddHours(-2), current.WindowStart);
        Assert.Equal(Now.UtcDateTime, current.WindowEnd);
    }

    [Fact]
    public async Task ASingleCandleIsMeasuredFromItsOwnOpen()
    {
        Configure(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._1H);
        Store("binance_futures_btcusdt_1h", 1, NineOClock, TimeSpan.FromHours(1)); // open 99, close 100

        var current = Assert.Single((await Handler.HandleAsync(new(null), Ct)).Items).Current;

        Assert.Equal(Math.Round((100m / 99m - 1m) * 100m, 2), current.ChangePercent);
        Assert.Equal(NineOClock, current.WindowStart);
        Assert.Equal(Now.UtcDateTime, current.WindowEnd);
    }

    [Fact]
    public async Task NothingConfiguredIsAnEmptyList()
    {
        var response = await Handler.HandleAsync(new(null), Ct);

        Assert.Empty(response.Items);
        Assert.Equal(Now.UtcDateTime, response.GeneratedAt);
    }
}
