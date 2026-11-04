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
}
