using Application.Interfaces;
using Application.Queries.Candles;
using Application.Services;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        var assembly = typeof(DependencyInjection).Assembly;
        var handlerTypes = assembly.GetTypes()
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)));

        foreach (var handlerType in handlerTypes)
        {
            var interfaceType = handlerType.GetInterfaces()
                .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>));

            services.AddTransient(interfaceType, handlerType);
        }

        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddScoped<ICandleReader, CandleReader>();

        return services;
    }

    /// <summary>
    /// Wraps the candle reader in a <see cref="HybridCache"/> decorator. Entries live in process memory; if an
    /// <c>IDistributedCache</c> (e.g. Redis) is registered, HybridCache uses it as the shared second level.
    /// </summary>
    public static IServiceCollection AddCandleCaching(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CandleCacheOptions>(configuration.GetSection(CandleCacheOptions.SectionName));
        services.AddHybridCache();
        services.TryAddSingleton(TimeProvider.System);

        services.AddScoped<CandleReader>();
        services.AddScoped<ICandleReader>(provider => new CachedCandleReader(
            provider.GetRequiredService<CandleReader>(),
            provider.GetRequiredService<HybridCache>(),
            provider.GetRequiredService<IOptions<CandleCacheOptions>>(),
            provider.GetRequiredService<TimeProvider>()));

        return services;
    }
}
