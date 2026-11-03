using Application.Research.Watch;
using Dapper;
using Npgsql;

namespace Infrastructure.Repositories;

/// <summary>
/// The symbols an administrator enabled for trading, read from <c>vault.trading_symbols</c>, the table the trading service keeps (it
/// owns it and is the only one that writes it). The watch and the admin API only read: choosing happens in the trading service,
/// behind a second-factor code. The table lives in the same database as the config schema (the trading service writes the audit log
/// there too).
/// </summary>
public sealed class TradedSymbolSource(string connectionString) : ITradedSymbolSource
{
    public async Task<IReadOnlySet<string>> GetEnabledAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            return (await connection.QueryAsync<string>(new CommandDefinition(
                    "SELECT symbol FROM vault.trading_symbols WHERE enabled", cancellationToken: cancellationToken)))
                .Select(s => s.ToUpperInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.InvalidSchemaName)
        {
            // The trading service makes its tables when it starts: where it never ran, nothing was ever enabled for trading. This is the
            // one failure that means "no symbols"; anything else (the database down, a refused login) is thrown, because answering it with
            // an empty set would make the watch drop every traded symbol over a hiccup.
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
