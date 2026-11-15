using Application.Exchange;
using Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Infrastructure.Exchange.Ctrader;

public static class CtraderExchangeRegistration
{
    /// <summary>
    /// The candle gateway and the symbol catalog of Pepperstone, through the cTrader Open API, for the collector. They are registered only when the
    /// settings to sign in are all there: without them the exchange is simply not served, so a Pepperstone series is left alone and a symbol cannot
    /// be listed for it, instead of the collector failing on a connection it cannot make.
    /// </summary>
    public static IServiceCollection AddCtraderExchange(this IServiceCollection services, IConfiguration configuration)
    {
        if (!AddConnection(services, configuration))
            return services;

        services.AddKeyedSingleton<ICandleGateway, CtraderCandleGateway>(ExchangeEnum.Pepperstone);
        services.TryAddSingleton<ICandleGateway, CandleGatewayRouter>();
        AddCatalog(services);

        return services;
    }

    /// <summary>
    /// Only what the admin API needs: asking which symbols the broker lists. Use the read only token (scope <c>accounts</c>) here: the API must
    /// never hold a token that can trade.
    /// </summary>
    public static IServiceCollection AddCtraderSymbolCatalog(this IServiceCollection services, IConfiguration configuration)
    {
        if (AddConnection(services, configuration))
            AddCatalog(services);

        return services;
    }

    private static bool AddConnection(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(CtraderExchangeConfigurations.SectionName);
        if (section.Get<CtraderExchangeConfigurations>() is not { IsConfigured: true })
            return false;

        services.Configure<CtraderExchangeConfigurations>(section);
        services.TryAddSingleton<CtraderConnection>();
        services.TryAddSingleton<ICtraderConnection>(provider => provider.GetRequiredService<CtraderConnection>());
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<CtraderSymbolDirectory>();
        return true;
    }

    private static void AddCatalog(IServiceCollection services)
    {
        services.TryAddKeyedSingleton<IExchangeSymbolCatalog, CtraderSymbolCatalog>(ExchangeEnum.Pepperstone);
        services.TryAddSingleton<IExchangeSymbolCatalog, ExchangeSymbolCatalogRouter>();
    }
}
