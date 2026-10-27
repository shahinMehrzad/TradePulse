using Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Application.UnitTests;

/// <summary>
/// The sign-in handlers need the identity infrastructure and the collector health query needs the client that asks the collector;
/// only the API has either. They are registered apart, so that the collector does not carry registrations it can never construct.
/// </summary>
public class HandlerRegistrationTests
{
    private static IEnumerable<Type> HandlersIn(IServiceCollection services) =>
        services.Where(d => d.ServiceType.IsGenericType && d.ServiceType.GetGenericTypeDefinition() == typeof(ICommandHandler<,>))
            .Select(d => d.ImplementationType!);

    private static IEnumerable<Type> EveryHandler() =>
        typeof(DependencyInjection).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommandHandler<,>)));

    [Fact]
    public void TheGeneralRegistrationHasNoApiOnlyHandler()
    {
        var registered = HandlersIn(new ServiceCollection().AddApplicationServices()).ToList();

        Assert.NotEmpty(registered);
        Assert.DoesNotContain(registered, t => DependencyInjection.ApiOnlyNamespaces.Contains(t.Namespace));
    }

    [Fact]
    public void TheApiOnlyRegistrationHasTheApiOnlyHandlersAndNothingElse()
    {
        var registered = HandlersIn(new ServiceCollection().AddApiOnlyApplicationServices()).ToList();

        Assert.NotEmpty(registered);
        Assert.All(registered, t => Assert.Contains(t.Namespace, DependencyInjection.ApiOnlyNamespaces));
        Assert.Contains(registered, t => t.Namespace == "Application.Queries.Collector");
    }

    [Fact]
    public void BetweenThemTheTwoRegisterEveryHandlerExactlyOnce()
    {
        // A new handler must end up in one of the two, never in neither (it would not be found) or in both.
        var services = new ServiceCollection().AddApplicationServices().AddApiOnlyApplicationServices();

        var registered = HandlersIn(services).OrderBy(t => t.FullName).ToList();

        Assert.Equal(EveryHandler().OrderBy(t => t.FullName), registered);
        Assert.Equal(registered.Count, registered.Distinct().Count());
    }
}
