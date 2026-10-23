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
    /// <summary>The namespace of the sign-in handlers; they need the identity infrastructure only the API has.</summary>
    private const string AuthNamespace = "Application.Commands.Auth";

    /// <summary>
    /// Every handler except the sign-in ones, which <see cref="AddAuthApplicationServices"/> adds. The collector uses this alone:
    /// it has no identity infrastructure, and a handler registered without its dependencies makes a container that validates
    /// itself on build (the Development default) refuse to start.
    /// </summary>
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        AddHandlers(services, type => !IsAuthHandler(type));

        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddScoped<ICandleReader, CandleReader>();
        services.TryAddSingleton(TimeProvider.System);

        return services;
    }

    /// <summary>The sign-in handlers (login, refresh token). Needs <c>AddAuthInfrastructure</c>.</summary>
    public static IServiceCollection AddAuthApplicationServices(this IServiceCollection services)
    {
        AddHandlers(services, IsAuthHandler);
        return services;
    }

    private static bool IsAuthHandler(Type type) => type.Namespace == AuthNamespace;

    private static void AddHandlers(IServiceCollection services, Func<Type, bool> include)
    {
        var handlerTypes = typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && include(t))
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)));

        foreach (var handlerType in handlerTypes)
        {
            var interfaceType = handlerType.GetInterfaces()
                .First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>));

            services.AddTransient(interfaceType, handlerType);
        }
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
