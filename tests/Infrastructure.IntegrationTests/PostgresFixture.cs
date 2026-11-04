using System.Text;
using Testcontainers.PostgreSql;

namespace Infrastructure.IntegrationTests;

/// <summary>One real Postgres container shared by every test in the collection. Needs Docker.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Every test has a database of its own and Npgsql keeps a pool of connections for each, so the idle ones add up over the whole run: the
    // default of 100 clients was reached once there were enough tests ("53300: sorry, too many clients already").
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine").WithCommand("-c", "max_connections=500").Build();

    public string ConnectionString => _container.GetConnectionString();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    /// <summary>Runs a script with psql inside the container, as the superuser, on one database. Throws with psql's own message when it fails.</summary>
    public async Task RunPsqlAsync(string database, string script)
    {
        var path = $"/tmp/{Guid.NewGuid():N}.sql";
        await _container.CopyAsync(Encoding.UTF8.GetBytes(script), path, ct: TestContext.Current.CancellationToken);
        var result = await _container.ExecAsync(["psql", "-U", "postgres", "-d", database, "-v", "ON_ERROR_STOP=1", "-q", "-f", path], TestContext.Current.CancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"psql failed: {result.Stderr}");
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
