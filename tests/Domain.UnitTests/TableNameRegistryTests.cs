using System.Text.RegularExpressions;
using Domain.Services;
using Domain.Enums;

namespace Domain.UnitTests;

public partial class TableNameRegistryTests
{
    [Theory]
    [InlineData(MarketTypeEnum.Futures, SymbolEnum.BTCUSDT, TimeFrameEnum._4H, "binance_futures_btcusdt_4h")]
    [InlineData(MarketTypeEnum.Spot, SymbolEnum.ETHUSDT, TimeFrameEnum._15M, "binance_spot_ethusdt_15m")]
    [InlineData(MarketTypeEnum.Spot, SymbolEnum.BTCUSDT, TimeFrameEnum._1D, "binance_spot_btcusdt_1d")]
    public void BuildsTheDocumentedTableName(MarketTypeEnum market, SymbolEnum symbol, TimeFrameEnum timeFrame, string expected)
    {
        var name = TableNameRegistry.GetCandleTableName(ExchangeEnum.Binance, market, symbol, timeFrame);

        // Other services read these tables by name, so the format is part of the contract.
        Assert.Equal(expected, name);
    }

    [Fact]
    public void EveryCombinationIsUnique()
    {
        var names = AllCombinations().ToList();

        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void EveryCombinationIsASafeUnquotedPostgresIdentifier()
    {
        // Table names are interpolated into SQL, so they must stay lowercase [a-z0-9_] and fit Postgres' 63 byte limit.
        // The pk_ prefix used for the primary key constraint has to fit as well.
        foreach (var name in AllCombinations())
        {
            Assert.Matches(SafeIdentifier(), name);
            Assert.True(("pk_" + name).Length <= 63, $"pk_{name} is longer than 63 characters");
        }
    }

    [Fact]
    public void RejectsUndefinedExchange()
    {
        var ex = Assert.Throws<ArgumentException>(() => TableNameRegistry.GetCandleTableName(
            (ExchangeEnum)99, MarketTypeEnum.Spot, SymbolEnum.BTCUSDT, TimeFrameEnum._1H));

        Assert.Equal("exchange", ex.ParamName);
    }

    [Fact]
    public void RejectsUndefinedMarketType()
    {
        var ex = Assert.Throws<ArgumentException>(() => TableNameRegistry.GetCandleTableName(
            ExchangeEnum.Binance, (MarketTypeEnum)99, SymbolEnum.BTCUSDT, TimeFrameEnum._1H));

        Assert.Equal("marketType", ex.ParamName);
    }

    [Fact]
    public void RejectsUndefinedSymbol()
    {
        var ex = Assert.Throws<ArgumentException>(() => TableNameRegistry.GetCandleTableName(
            ExchangeEnum.Binance, MarketTypeEnum.Spot, (SymbolEnum)999, TimeFrameEnum._1H));

        Assert.Equal("symbol", ex.ParamName);
    }

    [Fact]
    public void RejectsUndefinedTimeFrame()
    {
        var ex = Assert.Throws<ArgumentException>(() => TableNameRegistry.GetCandleTableName(
            ExchangeEnum.Binance, MarketTypeEnum.Spot, SymbolEnum.BTCUSDT, (TimeFrameEnum)12345));

        Assert.Equal("timeframe", ex.ParamName);
    }

    [Theory]
    [InlineData(SymbolEnum.BTCUSDT, "binance_futures_btcusdt_fundingrate")]
    [InlineData(SymbolEnum.ETHUSDT, "binance_futures_ethusdt_fundingrate")]
    public void BuildsTheFundingRateTableName(SymbolEnum symbol, string expected) =>
        Assert.Equal(expected, TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, symbol));

    [Theory]
    [InlineData(SymbolEnum.BTCUSDT, TimeFrameEnum._1H, "binance_futures_btcusdt_1h_openinterest")]
    [InlineData(SymbolEnum.SOLUSDT, TimeFrameEnum._15M, "binance_futures_solusdt_15m_openinterest")]
    public void BuildsTheOpenInterestTableName(SymbolEnum symbol, TimeFrameEnum timeFrame, string expected) =>
        Assert.Equal(expected, TableNameRegistry.GetOpenInterestTableName(ExchangeEnum.Binance, symbol, timeFrame));

    [Theory]
    [InlineData(SymbolEnum.BTCUSDT, TimeFrameEnum._1H, "binance_futures_btcusdt_1h_longshortratio")]
    [InlineData(SymbolEnum.SOLUSDT, TimeFrameEnum._15M, "binance_futures_solusdt_15m_longshortratio")]
    public void BuildsTheLongShortRatioTableName(SymbolEnum symbol, TimeFrameEnum timeFrame, string expected) =>
        Assert.Equal(expected, TableNameRegistry.GetLongShortRatioTableName(ExchangeEnum.Binance, symbol, timeFrame));

    [Theory]
    [InlineData(SymbolEnum.BTCUSDT, TimeFrameEnum._1H, "binance_futures_btcusdt_1h_takervolume")]
    [InlineData(SymbolEnum.SOLUSDT, TimeFrameEnum._15M, "binance_futures_solusdt_15m_takervolume")]
    public void BuildsTheTakerVolumeTableName(SymbolEnum symbol, TimeFrameEnum timeFrame, string expected) =>
        Assert.Equal(expected, TableNameRegistry.GetTakerVolumeTableName(ExchangeEnum.Binance, symbol, timeFrame));

    [Theory]
    [InlineData(SymbolEnum.BTCUSDT, "binance_futures_btcusdt_liquidation")]
    [InlineData(SymbolEnum.XRPUSDT, "binance_futures_xrpusdt_liquidation")]
    public void BuildsTheLiquidationTableName(SymbolEnum symbol, string expected) =>
        Assert.Equal(expected, TableNameRegistry.GetLiquidationTableName(ExchangeEnum.Binance, symbol));

    [Fact]
    public void NamesNeverCollideAcrossDataKinds()
    {
        var all = AllCombinations().Concat(AllMarketDataNames()).ToList();

        Assert.Equal(all.Count, all.Distinct().Count());
    }

    [Fact]
    public void MarketDataNamesAreSafeUnquotedPostgresIdentifiers()
    {
        // pk_ and ix_ constraint/index names are derived from the table name and must fit as well.
        foreach (var name in AllMarketDataNames())
        {
            Assert.Matches(SafeIdentifier(), name);
            Assert.True(("pk_" + name).Length <= 63, $"pk_{name} is longer than 63 characters");
            Assert.True(("ix_" + name + "_event_time").Length <= 63, $"ix_{name}_event_time is longer than 63 characters");
        }
    }

    [Fact]
    public void MarketDataTablesRejectUndefinedValues()
    {
        Assert.Throws<ArgumentException>(() => TableNameRegistry.GetFundingRateTableName((ExchangeEnum)99, SymbolEnum.BTCUSDT));
        Assert.Throws<ArgumentException>(() => TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, (SymbolEnum)999));
        Assert.Throws<ArgumentException>(() => TableNameRegistry.GetLiquidationTableName(ExchangeEnum.Binance, (SymbolEnum)999));
        Assert.Throws<ArgumentException>(() =>
            TableNameRegistry.GetOpenInterestTableName(ExchangeEnum.Binance, SymbolEnum.BTCUSDT, (TimeFrameEnum)12345));
    }

    [Fact]
    public void TheExchangesOwnNameGivesTheSameTableNamesTheEnumAlwaysDid()
    {
        // The symbol comes from the database now (as the exchange's name for it); the tables other services read must not move.
        foreach (var symbol in Enum.GetValues<SymbolEnum>())
        {
            var name = symbol.ToString();

            foreach (var market in Enum.GetValues<MarketTypeEnum>())
                foreach (var timeFrame in Enum.GetValues<TimeFrameEnum>())
                    Assert.Equal(
                        TableNameRegistry.GetCandleTableName(ExchangeEnum.Binance, market, symbol, timeFrame),
                        TableNameRegistry.GetCandleTableName(ExchangeEnum.Binance, market, name, timeFrame));

            Assert.Equal(TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, symbol), TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, name));
            Assert.Equal(TableNameRegistry.GetLiquidationTableName(ExchangeEnum.Binance, symbol), TableNameRegistry.GetLiquidationTableName(ExchangeEnum.Binance, name));
            Assert.Equal(TableNameRegistry.GetOpenInterestTableName(ExchangeEnum.Binance, symbol, TimeFrameEnum._1H), TableNameRegistry.GetOpenInterestTableName(ExchangeEnum.Binance, name, TimeFrameEnum._1H));
            Assert.Equal(TableNameRegistry.GetLongShortRatioTableName(ExchangeEnum.Binance, symbol, TimeFrameEnum._1H), TableNameRegistry.GetLongShortRatioTableName(ExchangeEnum.Binance, name, TimeFrameEnum._1H));
            Assert.Equal(TableNameRegistry.GetTakerVolumeTableName(ExchangeEnum.Binance, symbol, TimeFrameEnum._1H), TableNameRegistry.GetTakerVolumeTableName(ExchangeEnum.Binance, name, TimeFrameEnum._1H));
        }
    }

    [Theory]
    [InlineData("BTCUSDT", "btcusdt")]
    [InlineData("1000PEPEUSDT", "1000pepeusdt")]
    [InlineData("BTC-USDT-SWAP", "btcusdtswap")]
    [InlineData("btc_usdt", "btcusdt")]
    [InlineData("XBT/USD", "xbtusd")]
    public void ReducesTheExchangesNameToLowercaseLettersAndDigits(string exchangeSymbol, string expected)
    {
        Assert.True(TableNameRegistry.TryGetSymbolPart(ExchangeEnum.Binance, MarketTypeEnum.Futures, exchangeSymbol, out var part, out var reason), reason);

        Assert.Equal(expected, part);
        Assert.Equal($"binance_futures_{expected}_4h", TableNameRegistry.GetCandleTableName(ExchangeEnum.Binance, MarketTypeEnum.Futures, exchangeSymbol, TimeFrameEnum._4H));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("BTC USDT")]
    [InlineData("BTCUSDT; DROP TABLE x")]
    [InlineData("BTC\"USDT")]
    [InlineData("ÉTHUSDT")]
    [InlineData("BTC\nUSDT")]
    public void RefusesNamesThatAreNotSafeToPutInATableName(string? exchangeSymbol)
    {
        Assert.False(TableNameRegistry.TryGetSymbolPart(ExchangeEnum.Binance, MarketTypeEnum.Futures, exchangeSymbol, out var part, out var reason));

        Assert.Equal(string.Empty, part);
        Assert.NotEmpty(reason);
        Assert.Throws<ArgumentException>("symbol", () => TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, exchangeSymbol!));
    }

    [Fact]
    public void RefusesAnUnknownExchangeOrMarketWithTheReason()
    {
        Assert.False(TableNameRegistry.TryGetSymbolPart((ExchangeEnum)99, MarketTypeEnum.Futures, "BTCUSDT", out _, out var exchangeReason));
        Assert.False(TableNameRegistry.TryGetSymbolPart(ExchangeEnum.Binance, (MarketTypeEnum)99, "BTCUSDT", out _, out var marketReason));

        Assert.Equal(["unknown exchange", "unknown market"], [exchangeReason, marketReason]);
    }

    [Fact]
    public void ALongNameIsAcceptedExactlyAsFarAsEveryTableNameAndItsKeysStillFit()
    {
        var longest = Enumerable.Range(1, 40)
            .Select(n => new string('a', n))
            .Where(name => TableNameRegistry.TryGetSymbolPart(ExchangeEnum.Binance, MarketTypeEnum.Futures, name, out _, out _))
            .Last();

        // The last accepted name: every table name of every data kind and the pk_/ix_ names derived from it fit in 63 characters...
        var tables = Enum.GetValues<TimeFrameEnum>().SelectMany(t => new[]
        {
            TableNameRegistry.GetCandleTableName(ExchangeEnum.Binance, MarketTypeEnum.Futures, longest, t),
            TableNameRegistry.GetOpenInterestTableName(ExchangeEnum.Binance, longest, t),
            TableNameRegistry.GetLongShortRatioTableName(ExchangeEnum.Binance, longest, t),
            TableNameRegistry.GetTakerVolumeTableName(ExchangeEnum.Binance, longest, t),
        }).Append(TableNameRegistry.GetFundingRateTableName(ExchangeEnum.Binance, longest))
          .Append(TableNameRegistry.GetLiquidationTableName(ExchangeEnum.Binance, longest));
        Assert.All(tables, name =>
        {
            Assert.True(("pk_" + name).Length <= 63, $"pk_{name}");
            Assert.True(("ix_" + name + "_event_time").Length <= 63, $"ix_{name}_event_time");
        });

        // ...and one more character is refused.
        Assert.False(TableNameRegistry.TryGetSymbolPart(ExchangeEnum.Binance, MarketTypeEnum.Futures, longest + "a", out _, out _));
    }

    private static IEnumerable<string> AllMarketDataNames() =>
        from exchange in Enum.GetValues<ExchangeEnum>()
        from symbol in Enum.GetValues<SymbolEnum>()
        from name in new[]
        {
            TableNameRegistry.GetFundingRateTableName(exchange, symbol),
            TableNameRegistry.GetLiquidationTableName(exchange, symbol),
        }.Concat(Enum.GetValues<TimeFrameEnum>().Select(t => TableNameRegistry.GetOpenInterestTableName(exchange, symbol, t)))
        select name;

    private static IEnumerable<string> AllCombinations() =>
        from exchange in Enum.GetValues<ExchangeEnum>()
        from market in Enum.GetValues<MarketTypeEnum>()
        from symbol in Enum.GetValues<SymbolEnum>()
        from timeFrame in Enum.GetValues<TimeFrameEnum>()
        select TableNameRegistry.GetCandleTableName(exchange, market, symbol, timeFrame);

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SafeIdentifier();
}
