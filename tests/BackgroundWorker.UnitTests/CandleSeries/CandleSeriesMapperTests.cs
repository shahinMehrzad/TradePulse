using BackgroundWorker.CandleSeries;
using Domain.Entities;
using Domain.Enums;

namespace BackgroundWorker.UnitTests.CandleSeries;

public class CandleSeriesMapperTests
{
    private static CandleWorkerConfiguration Row(
        int id = 1, int exchange = (int)ExchangeEnum.Binance, int market = (int)MarketTypeEnum.Futures,
        int symbol = (int)SeededSymbol.BTCUSDT, int timeFrame = (int)TimeFrameEnum._4H, DateTime? start = null) => new()
    {
        Id = id,
        TimeFrameId = timeFrame,
        SymbolListing = new SymbolListing
        {
            ExchangeId = exchange, MarketId = market, SymbolId = symbol,
            ExchangeSymbol = Enum.IsDefined((SeededSymbol)symbol) ? ((SeededSymbol)symbol).ToString() : "NOSUCHUSDT"
        },
        StartDate = start ?? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified),
    };

    [Fact]
    public void IdsBecomeTheEnumMembersTheyAre()
    {
        var series = Assert.Single(CandleSeriesMapper.ToSeries([Row(market: 2, symbol: 3, timeFrame: 3600)]));

        Assert.Equal((MarketTypeEnum.Futures, SeededSymbol.ETHUSDT.Sym(), TimeFrameEnum._1H), (series.Series.MarketType, series.Series.Symbol, series.Series.Timeframe));
    }

    [Fact]
    public void TheStartDateIsReadAsUtc()
    {
        // The column has no zone and always holds UTC; the workers compare it with UtcNow and send it to Binance.
        var series = Assert.Single(CandleSeriesMapper.ToSeries([Row(start: new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Unspecified))]));

        Assert.Equal((new DateTime(2020, 1, 1), DateTimeKind.Utc), (series.StartDate, series.StartDate.Kind));
    }

    [Theory]
    [InlineData(99, 2, 3, 3600, "unknown exchange")]
    [InlineData(1, 99, 3, 3600, "unknown market")]
    [InlineData(1, 2, 3, 12345, "unknown time frame")]
    public void ARowItCannotNameIsSkippedAndReportedNotFatal(int exchange, int market, int symbol, int timeFrame, string reasonStart)
    {
        var good = Row(id: 1);
        var bad = Row(id: 2, exchange: exchange, market: market, symbol: symbol, timeFrame: timeFrame);
        var skipped = new List<(int Id, string Reason)>();

        var series = CandleSeriesMapper.ToSeries([bad, good], (row, reason) => skipped.Add((row.Id, reason)));

        Assert.Single(series);
        var (id, reason) = Assert.Single(skipped);
        Assert.Equal(2, id);
        Assert.StartsWith(reasonStart, reason);
    }

    [Fact]
    public void ARowWhoseSymbolIsNotListedIsSkippedAndReportedNotFatal()
    {
        var unlisted = Row(id: 2);
        unlisted.SymbolListing = null;
        var skipped = new List<(int Id, string Reason)>();

        var series = CandleSeriesMapper.ToSeries([Row(id: 1), unlisted], (row, reason) => skipped.Add((row.Id, reason)));

        Assert.Single(series);
        Assert.Equal([(2, "the symbol is not listed on this exchange market")], skipped);
    }

    [Fact]
    public void ARepeatedSeriesIsKeptOnce()
    {
        var series = CandleSeriesMapper.ToSeries([Row(id: 1), Row(id: 2)]);

        Assert.Single(series);
    }

    [Fact]
    public void NoRowsMeansNoSeries()
    {
        Assert.Empty(CandleSeriesMapper.ToSeries([]));
    }

    [Fact]
    public void EveryEnumMemberTheApiCanStoreIsMapped()
    {
        // The lookup tables mirror these enums, so any id the admin can pick has to survive the mapping.
        var rows = (from market in Enum.GetValues<MarketTypeEnum>()
                    from symbol in Enum.GetValues<SeededSymbol>()
                    from timeFrame in Enum.GetValues<TimeFrameEnum>()
                    select Row(market: (int)market, symbol: (int)symbol, timeFrame: (int)timeFrame)).ToList();

        var series = CandleSeriesMapper.ToSeries(rows);

        Assert.Equal(rows.Count, series.Count);
    }

    [Fact]
    public void EachExchangePicksItsOwnPlans()
    {
        var binance = TestPlans.Binance(MarketTypeEnum.Futures, SeededSymbol.BTCUSDT, TimeFrameEnum._1H);
        var other = binance with { Series = binance.Series with { Exchange = (ExchangeEnum)2 } };

        Assert.Equal([binance], new[] { binance, other }.ForExchange(ExchangeEnum.Binance));
    }
}
