using Binance.Net.Enums;
using Domain.Enums;
using Infrastructure.Configurations;
using Microsoft.Extensions.Configuration;

namespace BackgroundWorker.UnitTests;

/// <summary>
/// Guards the contract between our enums, the shipped appsettings.json and Binance.Net.
/// These fail when somebody adds an enum member or a symbol entry that the worker could not handle.
/// </summary>
public class WorkerSettingsContractTests
{
    private static BinanceExchangeConfigurations LoadSettings() =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build()
            .GetSection("BinanceExchangeConfigurations")
            .Get<BinanceExchangeConfigurations>()!;

    [Fact]
    public void ShippedSettingsBindToTheEnums()
    {
        var settings = LoadSettings();

        Assert.NotEmpty(settings.Symbols);
        Assert.True(settings.MaxLimit > 0);
        Assert.All(settings.Symbols.Values, s =>
        {
            Assert.True(Enum.IsDefined(s.Name));
            Assert.True(Enum.IsDefined(s.Interval));
            Assert.True(Enum.IsDefined(s.MarketType));
        });
    }

    [Fact]
    public void EntryKeysDescribeTheirContent()
    {
        foreach (var (key, symbol) in LoadSettings().Symbols)
            Assert.Equal($"{symbol.MarketType}_{symbol.Name}_{symbol.Interval.ToString().TrimStart('_')}", key);
    }

    [Fact]
    public void NoMarketSymbolAndIntervalIsConfiguredTwice()
    {
        var duplicates = LoadSettings().Symbols.Values
            .GroupBy(s => (s.MarketType, s.Name, s.Interval))
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    [Fact]
    public void StartDatesAreInThePast()
    {
        Assert.All(LoadSettings().Symbols.Values, s => Assert.True(s.StartDate < DateTime.UtcNow));
    }

    private static BinanceFuturesDataConfigurations LoadFuturesData() =>
        new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
            .Build()
            .GetSection("BinanceFuturesDataConfigurations")
            .Get<BinanceFuturesDataConfigurations>()!;

    [Fact]
    public void FuturesDataSettingsBindToTheEnums()
    {
        var settings = LoadFuturesData();

        Assert.NotEmpty(settings.FundingRates);
        Assert.NotEmpty(settings.OpenInterests);
        Assert.NotEmpty(settings.Liquidations);
        Assert.All(settings.FundingRates.Values, f => Assert.True(Enum.IsDefined(f.Name)));
        Assert.All(settings.OpenInterests.Values, o =>
        {
            Assert.True(Enum.IsDefined(o.Name));
            Assert.True(Enum.IsDefined(o.Interval));
        });
        Assert.All(settings.Liquidations.Values, l => Assert.True(Enum.IsDefined(l.Name)));
    }

    [Fact]
    public void ConfiguredOpenInterestIntervalsAreOnesBinanceOffers()
    {
        // An unsupported interval is skipped (with an error) at runtime; fail here before it gets shipped.
        foreach (var entry in LoadFuturesData().OpenInterests.Values)
            Assert.True(BinancePeriodIntervals.TryMap(entry.Interval, out _), $"{entry.Name} {entry.Interval} is not available from Binance");
    }

    [Fact]
    public void FuturesDataEntryKeysDescribeTheirContent()
    {
        var settings = LoadFuturesData();

        foreach (var (key, entry) in settings.FundingRates)
            Assert.Equal(entry.Name.ToString(), key);

        foreach (var (key, entry) in settings.OpenInterests)
            Assert.Equal($"{entry.Name}_{entry.Interval.ToString().TrimStart('_')}", key);

        foreach (var (key, entry) in settings.Liquidations)
            Assert.Equal(entry.Name.ToString(), key);
    }

    [Fact]
    public void NoFuturesDataEntryIsConfiguredTwice()
    {
        var settings = LoadFuturesData();

        Assert.Equal(settings.FundingRates.Count, settings.FundingRates.Values.Select(f => f.Name).Distinct().Count());
        Assert.Equal(settings.OpenInterests.Count, settings.OpenInterests.Values.Select(o => (o.Name, o.Interval)).Distinct().Count());
        Assert.Equal(settings.Liquidations.Count, settings.Liquidations.Values.Select(l => l.Name).Distinct().Count());
    }

    [Fact]
    public void FundingRateStartDatesAreInThePast()
    {
        Assert.All(LoadFuturesData().FundingRates.Values, f => Assert.True(f.StartDate < DateTime.UtcNow));
    }

    [Theory]
    [MemberData(nameof(AllTimeFrames))]
    public void EveryTimeFrameHasABinanceKlineInterval(TimeFrameEnum timeFrame)
    {
        // The worker casts TimeFrameEnum straight to KlineInterval, which only works while the numbers match.
        var interval = (KlineInterval)(int)timeFrame;

        Assert.True(Enum.IsDefined(interval), $"{timeFrame} ({(int)timeFrame}s) is not a Binance kline interval");
    }

    public static TheoryData<TimeFrameEnum> AllTimeFrames() => new(Enum.GetValues<TimeFrameEnum>());
}
