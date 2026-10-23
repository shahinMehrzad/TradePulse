using System.Reflection;
using Application.Interfaces;
using Domain.Entities;

namespace BackgroundWorker.UnitTests;

/// <summary>
/// The layering, enforced. The compiler records an assembly reference only for assemblies whose types the code really uses, so
/// an assembly's reference list says what it depends on, whatever the project files allow.
/// </summary>
public class ArchitectureTests
{
    // Talking to an exchange and to storage is the job of Infrastructure, behind ports.
    private static readonly string[] ExchangeAndStorage =
        ["Binance.Net", "CryptoExchange.Net", "Npgsql", "Dapper", "Microsoft.EntityFrameworkCore"];

    private static readonly string[] IdentityAndTokens =
        ["Microsoft.AspNetCore.Identity", "System.IdentityModel.Tokens.Jwt", "Microsoft.IdentityModel"];

    private static IEnumerable<string> References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!);

    private static void AssertNoneOf(Assembly assembly, params IEnumerable<string>[] forbiddenGroups)
    {
        var forbidden = forbiddenGroups.SelectMany(g => g).ToList();
        var found = References(assembly).Where(r => forbidden.Any(f => r == f || r.StartsWith(f + ".", StringComparison.Ordinal))).ToList();

        Assert.True(found.Count == 0, $"{assembly.GetName().Name} must not depend on: {string.Join(", ", found)}");
    }

    [Fact]
    public void TheCollectorKnowsNoExchangeLibraryAndNoDatabaseDriver()
    {
        // It asks ports (ICandleGateway, ILiveCandleStream, ...) and Infrastructure answers them.
        AssertNoneOf(typeof(BackgroundWorker.Collection.Candles.BinanceCandleWorker).Assembly, ExchangeAndStorage);
    }

    [Fact]
    public void ApplicationKnowsNoExchangeNoStorageAndNoIdentityFramework()
    {
        AssertNoneOf(typeof(ICommandHandler<,>).Assembly, ExchangeAndStorage, IdentityAndTokens, ["Infrastructure"]);
    }

    [Fact]
    public void DomainDependsOnNothingButTheBaseLibrary()
    {
        var outsiders = References(typeof(Candle).Assembly)
            .Where(r => r != "mscorlib" && r != "netstandard" && !r.StartsWith("System", StringComparison.Ordinal))
            .ToList();

        Assert.True(outsiders.Count == 0, $"Domain must not depend on: {string.Join(", ", outsiders)}");
    }
}
