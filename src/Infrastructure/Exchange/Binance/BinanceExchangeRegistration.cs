using Application.Exchange;
using Infrastructure.Configurations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Exchange.Binance;

public static class BinanceExchangeRegistration
{
    /// <summary>
    /// Everything that talks to Binance: the client library, the shared request budget (Binance counts weight per IP, so every
    /// gateway draws from the same one) and the gateways the collectors use. Only the collector needs it; WebApi does not.
    /// </summary>
    public static IServiceCollection AddBinanceExchange(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<BinanceExchangeConfigurations>(configuration.GetSection(nameof(BinanceExchangeConfigurations)));
        services.AddBinance();
        services.AddSingleton<BinanceRateLimiters>();

        services.AddSingleton<ICandleGateway, BinanceCandleGateway>();
        services.AddSingleton<IFundingRateGateway, BinanceFundingRateGateway>();
        services.AddSingleton<IOpenInterestGateway, BinanceOpenInterestGateway>();

        return services;
    }
}
