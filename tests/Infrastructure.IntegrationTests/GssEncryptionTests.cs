using Npgsql;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// The compose file ends every connection string with <c>GSS Encryption Mode=Disable</c> so that Npgsql does not look for a Kerberos library
/// the images do not have. This is the proof that the server accepts a connection made that way, and that the setting is what Npgsql thinks it is.
/// </summary>
[Collection(PostgresCollection.Name)]
public class GssEncryptionTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AConnectionWithGssEncryptionDisabledWorks()
    {
        var connectionString = postgres.ConnectionString + ";GSS Encryption Mode=Disable";
        Assert.Equal(GssEncryptionMode.Disable, new NpgsqlConnectionStringBuilder(connectionString).GssEncryptionMode);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT 1", connection);

        Assert.Equal(1, await command.ExecuteScalarAsync(Ct));
    }
}
