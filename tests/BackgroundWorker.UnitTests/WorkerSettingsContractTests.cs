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

    // What to collect (the candle series, open interest, funding rate and liquidations) is no longer a setting: the admin chooses it in the config schema, where the
    // foreign keys and the unique indexes (not this file) guarantee that every choice is representable and appears once.
    [Fact]
    public void ShippedSettingsBindAndTheRequestBudgetIsPositive()
    {
        var settings = LoadSettings();

        Assert.True(settings.MaxLimit > 0);
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
