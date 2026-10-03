using Domain.Repositories;
using Infrastructure.Configurations;
using Infrastructure.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.AddScoped<ICandleRepository>(provider => new CandleRepository(connectionString));

        services.Configure<BinanceExchangeConfigurations>(configuration.GetSection(nameof(BinanceExchangeConfigurations)));

        services.AddBinance();

        return services;
    }
}
