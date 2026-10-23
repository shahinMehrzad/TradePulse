using Application.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace Application.UnitTests;

/// <summary>
/// The sign-in handlers need the identity infrastructure, which only the API has. They are registered apart, so that the collector
/// (which has none) does not carry registrations it can never construct.
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
    public void TheGeneralRegistrationHasNoSignInHandler()
    {
        var registered = HandlersIn(new ServiceCollection().AddApplicationServices()).ToList();

        Assert.NotEmpty(registered);
        Assert.DoesNotContain(registered, t => t.Namespace == "Application.Commands.Auth");
    }

    [Fact]
    public void TheSignInRegistrationHasTheSignInHandlersAndNothingElse()
    {
        var registered = HandlersIn(new ServiceCollection().AddAuthApplicationServices()).ToList();

        Assert.NotEmpty(registered);
        Assert.All(registered, t => Assert.Equal("Application.Commands.Auth", t.Namespace));
    }

    [Fact]
    public void BetweenThemTheTwoRegisterEveryHandlerExactlyOnce()
    {
        // A new handler must end up in one of the two, never in neither (it would not be found) or in both.
        var services = new ServiceCollection().AddApplicationServices().AddAuthApplicationServices();

        var registered = HandlersIn(services).OrderBy(t => t.FullName).ToList();

        Assert.Equal(EveryHandler().OrderBy(t => t.FullName), registered);
        Assert.Equal(registered.Count, registered.Distinct().Count());
    }
}
