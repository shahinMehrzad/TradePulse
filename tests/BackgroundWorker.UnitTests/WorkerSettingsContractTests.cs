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
