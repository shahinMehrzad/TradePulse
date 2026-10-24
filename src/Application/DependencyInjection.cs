using Application.Interfaces;
using Application.Queries.Candles;
using Application.Queries.Dashboard;
using Application.Services;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Application;

public static class DependencyInjection
{
    
    public static readonly string[] ApiOnlyNamespaces = ["Application.Commands.Auth", "Application.Queries.Collector"];
        

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        AddHandlers(services, type => !IsApiOnlyHandler(type));

        services.AddScoped<ICommandDispatcher, CommandDispatcher>();
        services.AddScoped<ICandleReader, CandleReader>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<DashboardSnapshotBuilder>();
        services.AddScoped<AuditTrail>();

        return services;
    }

    public static IServiceCollection AddApiOnlyApplicationServices(this IServiceCollection services)
    {
        AddHandlers(services, IsApiOnlyHandler);
        return services;
    }

    private static bool IsApiOnlyHandler(Type type) => ApiOnlyNamespaces.Contains(type.Namespace);

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
