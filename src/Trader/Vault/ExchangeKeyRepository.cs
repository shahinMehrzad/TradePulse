using Dapper;
using Npgsql;

namespace Trader.Vault;

public static class KeyStatus
{
    /// <summary>The key pair exists; the user has not yet given the API key Binance made for the public key.</summary>
    public const string PendingApiKey = "PendingApiKey";

    /// <summary>Checked against Binance: signs correctly, no withdrawals, locked to an IP, futures on.</summary>
    public const string Active = "Active";

    /// <summary>Binance answered, but something is wrong (see the problems). Fix it at Binance and verify again.</summary>
    public const string Rejected = "Rejected";
}

public sealed record ExchangeKeyRow(
    Guid Id, Guid OwnerUserId, string Label, string Exchange, string Algorithm, string PublicKeyPem,
    byte[] PrivateKeyEncrypted, string? ApiKey, string Status, string? Problems, DateTime? VerifiedAt, DateTime CreatedAt, string Environment);

/// <summary>
/// The table of the exchange keys (schema <c>vault</c>, made by this program). Every lookup that belongs to a user takes the owner,
/// so a key of somebody else is simply not found. Times are <c>timestamp</c> (no zone), always UTC, like everywhere else.
/// </summary>
public sealed class ExchangeKeyRepository(NpgsqlDataSource dataSource)
{
    public const int MaxKeysPerUser = 5;

    private const string Columns = """
        id AS Id, owner_user_id AS OwnerUserId, label AS Label, exchange AS Exchange, algorithm AS Algorithm,
        public_key_pem AS PublicKeyPem, private_key_encrypted AS PrivateKeyEncrypted, api_key AS ApiKey, status AS Status,
        problems AS Problems, verified_at AS VerifiedAt, created_at AS CreatedAt, environment AS Environment
        """;

    public static async Task EnsureSchemaAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            CREATE SCHEMA IF NOT EXISTS vault;
            CREATE TABLE IF NOT EXISTS vault.exchange_keys (
                id                    uuid PRIMARY KEY,
                owner_user_id         uuid NOT NULL,
                label                 text NOT NULL,
                exchange              text NOT NULL,
                algorithm             text NOT NULL,
                public_key_pem        text NOT NULL,
                private_key_encrypted bytea NOT NULL,
                api_key               text NULL,
                status                text NOT NULL,
                problems              text NULL,
                verified_at           timestamp NULL,
                created_at            timestamp NOT NULL
            );
            ALTER TABLE vault.exchange_keys ADD COLUMN IF NOT EXISTS environment text NOT NULL DEFAULT 'Live';
            CREATE INDEX IF NOT EXISTS ix_exchange_keys_owner ON vault.exchange_keys (owner_user_id);
            """, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<ExchangeKeyRow>> ListAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<ExchangeKeyRow>(new CommandDefinition(
            $"SELECT {Columns} FROM vault.exchange_keys WHERE owner_user_id = @owner ORDER BY created_at",
            new { owner }, cancellationToken: cancellationToken))).Select(AsUtc).ToList();
    }

    public async Task<ExchangeKeyRow?> FindAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ExchangeKeyRow>(new CommandDefinition(
            $"SELECT {Columns} FROM vault.exchange_keys WHERE id = @id AND owner_user_id = @owner",
            new { id, owner }, cancellationToken: cancellationToken));
        return row is null ? null : AsUtc(row);
    }

    public async Task<int> CountAsync(Guid owner, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT count(*) FROM vault.exchange_keys WHERE owner_user_id = @owner", new { owner }, cancellationToken: cancellationToken));
    }

    public async Task AddAsync(ExchangeKeyRow row, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO vault.exchange_keys
                (id, owner_user_id, label, exchange, algorithm, public_key_pem, private_key_encrypted, api_key, status, problems, verified_at, created_at, environment)
            VALUES
                (@Id, @OwnerUserId, @Label, @Exchange, @Algorithm, @PublicKeyPem, @PrivateKeyEncrypted, @ApiKey, @Status, @Problems, @VerifiedAt, @CreatedAt, @Environment)
            """, ForWrite(row), cancellationToken: cancellationToken));
    }

    public async Task SetApiKeyAsync(Guid owner, Guid id, string apiKey, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE vault.exchange_keys SET api_key = @apiKey, status = @status, problems = NULL, verified_at = NULL WHERE id = @id AND owner_user_id = @owner",
            new { id, owner, apiKey, status = KeyStatus.PendingApiKey }, cancellationToken: cancellationToken));
    }

    public async Task SetVerificationAsync(Guid owner, Guid id, string status, IReadOnlyList<string> problems, DateTime verifiedAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE vault.exchange_keys SET status = @status, problems = @problems, verified_at = @verifiedAt WHERE id = @id AND owner_user_id = @owner",
            new
            {
                id, owner, status,
                problems = problems.Count == 0 ? null : string.Join('\n', problems),
                verifiedAt = DateTime.SpecifyKind(verifiedAtUtc, DateTimeKind.Unspecified)
            }, cancellationToken: cancellationToken));
    }

    /// <returns>True when a key was removed. The private key goes with the row; there is no other copy.</returns>
    public async Task<bool> DeleteAsync(Guid owner, Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM vault.exchange_keys WHERE id = @id AND owner_user_id = @owner", new { id, owner }, cancellationToken: cancellationToken)) == 1;
    }

    private static object ForWrite(ExchangeKeyRow row) => new
    {
        row.Id, row.OwnerUserId, row.Label, row.Exchange, row.Algorithm, row.PublicKeyPem, row.PrivateKeyEncrypted, row.ApiKey, row.Status, row.Problems, row.Environment,
        // Npgsql refuses Kind=Utc for a timestamp column.
        VerifiedAt = row.VerifiedAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Unspecified) : (DateTime?)null,
        CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Unspecified)
    };

    private static ExchangeKeyRow AsUtc(ExchangeKeyRow row) => row with
    {
        CreatedAt = DateTime.SpecifyKind(row.CreatedAt, DateTimeKind.Utc),
        VerifiedAt = row.VerifiedAt is { } at ? DateTime.SpecifyKind(at, DateTimeKind.Utc) : null
    };
}
