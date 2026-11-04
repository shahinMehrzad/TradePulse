using Microsoft.Extensions.Configuration;
using Trader.Trading;

namespace Trader.IntegrationTests;

/// <summary>The ceiling of the symbols is what the owner of the server wrote, not that plus a default nobody asked for.</summary>
public class TradingOptionsTests
{
    private static TradingOptions Bind(params (string Key, string Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>($"Trading:{s.Key}", s.Value)))
            .Build()
            .GetSection(TradingOptions.SectionName)
            .Get<TradingOptions>() ?? new TradingOptions();

    [Fact]
    public void TheSymbolsTheOwnerListsAreTheWholeCeilingWithNothingAddedToThem()
    {
        var options = Bind(("AllowedSymbols:0", "SOLUSDT"), ("AllowedSymbols:1", "XRPUSDT"));

        Assert.Equal(["SOLUSDT", "XRPUSDT"], options.AllowedSymbols);
    }

    [Fact]
    public void ListingTheDefaultsAgainDoesNotRepeatThem()
    {
        var options = Bind(("AllowedSymbols:0", "BTCUSDT"), ("AllowedSymbols:1", "ETHUSDT"));

        Assert.Equal(["BTCUSDT", "ETHUSDT"], options.AllowedSymbols);
    }
}
