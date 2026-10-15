using Application.Interfaces;
using Application.Services;
using Microsoft.Extensions.DependencyInjection;

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

        return services;
    }
}
