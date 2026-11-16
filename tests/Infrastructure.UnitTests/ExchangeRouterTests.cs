using Application.Exchange;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Exchange;
using Infrastructure.Exchange.Binance;
using Infrastructure.Exchange.Ctrader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.UnitTests;

/// <summary>The routers hand a request to the adapter of the exchange asked about, and refuse an exchange nobody registered.</summary>
public class ExchangeRouterTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly CandleSeries Binance = new(ExchangeEnum.Binance, MarketTypeEnum.Futures, new ExchangeSymbol("BTCUSDT"), TimeFrameEnum._1H);
    private static readonly CandleSeries Pepperstone = new(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol("EURUSD"), TimeFrameEnum._1H);

    private sealed class RecordingGateway : ICandleGateway
    {
        public List<CandleSeries> Asked { get; } = [];

        public Task<ExchangeResult<ExchangePage<Candle>>> GetCandlesAsync(CandleSeries series, DateTime from, DateTime? to, CancellationToken cancellationToken)
        {
            Asked.Add(series);
            return Task.FromResult(ExchangeResult<ExchangePage<Candle>>.Ok(new ExchangePage<Candle>([], HasMore: false)));
        }
    }

    private sealed class RecordingCatalog : IExchangeSymbolCatalog
    {
        public List<ExchangeEnum> Asked { get; } = [];

        public Task<ExchangeResult<ExchangeSymbol?>> FindTradingAsync(ExchangeEnum exchange, MarketTypeEnum market, ExchangeSymbol symbol, CancellationToken cancellationToken)
        {
            Asked.Add(exchange);
            return Task.FromResult(ExchangeResult<ExchangeSymbol?>.Ok(symbol));
        }
    }

    [Fact]
    public async Task ACandleRequestGoesToTheGatewayOfItsOwnExchangeOnly()
    {
        var binance = new RecordingGateway();
        var pepperstone = new RecordingGateway();
        var services = new ServiceCollection()
            .AddKeyedSingleton<ICandleGateway>(ExchangeEnum.Binance, binance)
            .AddKeyedSingleton<ICandleGateway>(ExchangeEnum.Pepperstone, pepperstone)
            .BuildServiceProvider();
        var router = new CandleGatewayRouter(services);

        await router.GetCandlesAsync(Pepperstone, DateTime.UtcNow, null, Ct);
        await router.GetCandlesAsync(Binance, DateTime.UtcNow, null, Ct);
        await router.GetCandlesAsync(Binance, DateTime.UtcNow, null, Ct);

        Assert.Equal([Pepperstone], pepperstone.Asked);
        Assert.Equal([Binance, Binance], binance.Asked);
    }

    [Fact]
    public async Task AnExchangeWithoutAGatewayIsNotServedAndNotGuessed()
    {
        var services = new ServiceCollection().AddKeyedSingleton<ICandleGateway>(ExchangeEnum.Binance, new RecordingGateway()).BuildServiceProvider();
        var router = new CandleGatewayRouter(services);

        Assert.True(router.Serves(ExchangeEnum.Binance));
        Assert.False(router.Serves(ExchangeEnum.Pepperstone));
        await Assert.ThrowsAsync<NotSupportedException>(() => router.GetCandlesAsync(Pepperstone, DateTime.UtcNow, null, Ct));
    }

    [Fact]
    public async Task ASymbolCatalogIsFoundByExchangeAndAnUnknownExchangeIsNotServed()
    {
        var binance = new RecordingCatalog();
        var services = new ServiceCollection().AddKeyedSingleton<IExchangeSymbolCatalog>(ExchangeEnum.Binance, binance).BuildServiceProvider();
        var router = new ExchangeSymbolCatalogRouter(services);

        await router.FindTradingAsync(ExchangeEnum.Binance, MarketTypeEnum.Futures, new ExchangeSymbol("BTCUSDT"), Ct);

        Assert.Equal([ExchangeEnum.Binance], binance.Asked);
        Assert.True(router.Serves(ExchangeEnum.Binance));
        Assert.False(router.Serves(ExchangeEnum.Pepperstone));
        await Assert.ThrowsAsync<NotSupportedException>(
            () => router.FindTradingAsync(ExchangeEnum.Pepperstone, MarketTypeEnum.Cfd, new ExchangeSymbol("EURUSD"), Ct));
    }

    [Fact]
    public void TheBinanceRegistrationsGiveTheRoutersAndTheKeyedAdapters()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddLogging();
        services.AddBinanceExchange(configuration);
        services.AddBinanceSymbolCatalog();
        using var provider = services.BuildServiceProvider();

        var gateway = provider.GetRequiredService<ICandleGateway>();
        var catalog = provider.GetRequiredService<IExchangeSymbolCatalog>();

        Assert.IsType<CandleGatewayRouter>(gateway);
        Assert.IsType<ExchangeSymbolCatalogRouter>(catalog);
        Assert.True(gateway.Serves(ExchangeEnum.Binance));
        Assert.False(gateway.Serves(ExchangeEnum.Pepperstone));
        Assert.True(catalog.Serves(ExchangeEnum.Binance));
        Assert.False(catalog.Serves(ExchangeEnum.Pepperstone));
    }

    private static IConfiguration Ctrader(bool complete) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["CtraderExchangeConfigurations:ClientId"] = "id",
        ["CtraderExchangeConfigurations:ClientSecret"] = "secret",
        ["CtraderExchangeConfigurations:AccessToken"] = complete ? "token" : null,
        ["CtraderExchangeConfigurations:AccountId"] = "1",
    }).Build();

    [Fact]
    public async Task PepperstoneIsServedOnlyWhenTheSettingsToSignInAreAllThere()
    {
        foreach (var complete in new[] { false, true })
        {
            var services = new ServiceCollection().AddLogging();
            services.AddBinanceExchange(Ctrader(complete));
            services.AddBinanceSymbolCatalog();
            services.AddCtraderExchange(Ctrader(complete));
            await using var provider = services.BuildServiceProvider();

            Assert.Equal(complete, provider.GetRequiredService<ICandleGateway>().Serves(ExchangeEnum.Pepperstone));
            Assert.Equal(complete, provider.GetRequiredService<IExchangeSymbolCatalog>().Serves(ExchangeEnum.Pepperstone));
            Assert.True(provider.GetRequiredService<ICandleGateway>().Serves(ExchangeEnum.Binance));
            Assert.Equal(complete, provider.GetRequiredService<IExchangeProbe>().Serves(ExchangeEnum.Pepperstone));
            Assert.True(provider.GetRequiredService<IExchangeProbe>().Serves(ExchangeEnum.Binance));
        }
    }

    [Fact]
    public async Task SettingsThatDeployLeavesEmptyMeanNotConfiguredAndNotACrash()
    {
        // deploy/docker-compose.yml passes every setting, empty when deploy/.env has none: an empty account id must not stop the host.
        var empty = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CtraderExchangeConfigurations:ClientId"] = "",
            ["CtraderExchangeConfigurations:ClientSecret"] = "",
            ["CtraderExchangeConfigurations:AccessToken"] = "",
            ["CtraderExchangeConfigurations:AccountId"] = "",
            ["CtraderExchangeConfigurations:Environment"] = "Demo",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddBinanceExchange(empty);
        services.AddCtraderExchange(empty);
        await using var provider = services.BuildServiceProvider();

        Assert.False(provider.GetRequiredService<ICandleGateway>().Serves(ExchangeEnum.Pepperstone));
    }

    [Fact]
    public async Task TheAdminApisCatalogServesPepperstoneToo()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddBinanceSymbolCatalog();
        services.AddCtraderSymbolCatalog(Ctrader(complete: true));
        await using var provider = services.BuildServiceProvider();

        var catalog = provider.GetRequiredService<IExchangeSymbolCatalog>();

        Assert.True(catalog.Serves(ExchangeEnum.Pepperstone));
        Assert.True(catalog.Serves(ExchangeEnum.Binance));
    }

    [Fact]
    public async Task TheEndOfTheTokenAndTheWarningPeriodAreReadFromSettingsTheWayComposeGivesThem()
    {
        // deploy/docker-compose.yml passes them as plain text: a date with an offset and days.hours:minutes:seconds.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CtraderExchangeConfigurations:ClientId"] = "id",
            ["CtraderExchangeConfigurations:ClientSecret"] = "secret",
            ["CtraderExchangeConfigurations:AccessToken"] = "token",
            ["CtraderExchangeConfigurations:AccountId"] = "1",
            ["CtraderExchangeConfigurations:AccessTokenExpiresAt"] = "2026-12-16T23:58:57.7375540+00:00",
            ["CtraderExchangeConfigurations:AccessTokenWarnBefore"] = "3.00:00:00",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddCtraderExchange(configuration);
        await using var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CtraderExchangeConfigurations>>().Value;

        Assert.Equal(new DateTimeOffset(2026, 12, 16, 23, 58, 57, TimeSpan.Zero).AddTicks(7375540), settings.AccessTokenExpiresAt);
        Assert.Equal(TimeSpan.FromDays(3), settings.AccessTokenWarnBefore);
    }

    [Fact]
    public async Task AnEmptyEndOfTheTokenMeansNoneAndTheWarningPeriodStaysOneDayWhenNotGiven()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CtraderExchangeConfigurations:ClientId"] = "id",
            ["CtraderExchangeConfigurations:ClientSecret"] = "secret",
            ["CtraderExchangeConfigurations:AccessToken"] = "token",
            ["CtraderExchangeConfigurations:AccountId"] = "1",
            ["CtraderExchangeConfigurations:AccessTokenExpiresAt"] = "",
        }).Build();
        var services = new ServiceCollection().AddLogging();
        services.AddCtraderExchange(configuration);
        await using var provider = services.BuildServiceProvider();

        var settings = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<CtraderExchangeConfigurations>>().Value;

        Assert.Null(settings.AccessTokenExpiresAt);
        Assert.Equal(TimeSpan.FromDays(1), settings.AccessTokenWarnBefore);
    }
}
