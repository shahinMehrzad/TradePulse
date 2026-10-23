using Application.Commands.Auth;
using Application.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BackgroundWorker.UnitTests;

/// <summary>
/// The collector's real registrations, built the way the Development environment builds them: with every registration checked
/// that it can be constructed. Production does not check, so a registration with a missing dependency stays hidden there until
/// something resolves it, and then breaks at run time.
/// </summary>
public class CollectorCompositionTests
{
    private static ServiceProvider Build()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCollector(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }

    [Fact]
    public void EveryRegistrationOfTheCollectorCanBeConstructed()
    {
        using var provider = Build();   // throws, naming the registration, when one cannot

        Assert.NotNull(provider);
    }

    [Fact]
    public void TheCollectorHasNoSignInHandlersBecauseItHasNoIdentityInfrastructure()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        Assert.Null(scope.ServiceProvider.GetService<ICommandHandler<LoginCommand, Application.Dtos.Auth.AuthResponseDto>>());
    }
}
