using Npgsql;

namespace Infrastructure.UnitTests;

/// <summary>
/// The connection strings of <c>deploy/docker-compose.yml</c> all say <c>GSS Encryption Mode=Disable</c>. The images have no Kerberos library, so
/// without it every program prints "Cannot load library libgssapi_krb5.so.2" at each start. A new service with a database must say it too.
/// </summary>
public class ComposeConnectionStringTests
{
    private static string[] ConnectionStrings()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotNetTradePulse.slnx")))
            directory = directory.Parent;
        var compose = Path.Combine(directory?.FullName ?? throw new FileNotFoundException("The solution folder was not found."), "deploy", "docker-compose.yml");

        return File.ReadAllLines(compose)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("ConnectionStrings__", StringComparison.Ordinal) && line.Contains("Host=", StringComparison.Ordinal))
            .ToArray();
    }

    [Fact]
    public void EveryConnectionStringDisablesGssEncryption()
    {
        var lines = ConnectionStrings();

        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.EndsWith(";GSS Encryption Mode=Disable", line));
    }

    [Fact]
    public void TheSettingIsOneNpgsqlKnows()
    {
        Assert.Equal(GssEncryptionMode.Disable, new NpgsqlConnectionStringBuilder("Host=h;GSS Encryption Mode=Disable").GssEncryptionMode);
    }

    /// <summary>The lines of one service of the compose file (from its name at two spaces to the next service).</summary>
    private static string[] ServiceLines(string service)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotNetTradePulse.slnx")))
            directory = directory.Parent;
        var lines = File.ReadAllLines(Path.Combine(directory?.FullName ?? throw new FileNotFoundException("The solution folder was not found."), "deploy", "docker-compose.yml"));

        var start = Array.FindIndex(lines, l => l == $"  {service}:");
        Assert.True(start >= 0, $"The service {service} is not in the compose file.");
        var end = Array.FindIndex(lines, start + 1, l => l.StartsWith("  ", StringComparison.Ordinal) && !l.StartsWith("   ", StringComparison.Ordinal) && l.TrimEnd().EndsWith(':'));

        return lines[start..(end < 0 ? lines.Length : end)];
    }

    [Theory]
    [InlineData("collector", "CtraderExchangeConfigurations__AccessTokenExpiresAt")]
    [InlineData("collector", "CtraderExchangeConfigurations__AccessTokenWarnBefore")]
    [InlineData("collector", "CtraderExchangeConfigurations__AccessToken")]
    [InlineData("api", "CtraderExchangeConfigurations__AccessToken")]
    public void TheCTraderSettingsReachTheServicesThatUseThem(string service, string setting)
    {
        // The collector judges the token's life and sends the alerts; the API only asks cTrader whether a symbol exists, so it has no need of the end date.
        Assert.Contains(ServiceLines(service), line => line.Trim().StartsWith(setting + ":", StringComparison.Ordinal));
    }

    [Fact]
    public void TheApiDoesNotGetTheSettingsOnlyTheCollectorUses()
    {
        Assert.DoesNotContain(ServiceLines("api"), line => line.Contains("AccessTokenExpiresAt", StringComparison.Ordinal));
    }
}
