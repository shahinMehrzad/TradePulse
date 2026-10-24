using Dapper;
using Domain.Repositories;
using Infrastructure.Helpers;
using Npgsql;

namespace Infrastructure.Repositories.TimeSeries;

/// <summary>
/// Generic binary-COPY writer shared by every non-candle time series. Same strategy as <c>CandleRepository</c>:
/// COPY into a temp table, then insert with <c>ON CONFLICT DO NOTHING</c> so re-fetching a range is always safe.
/// <paramref name="tableName"/> arguments are interpolated into SQL and must come from <c>TableNameRegistry</c>.
/// </summary>
public sealed class TimeSeriesRepository<T>(string connectionString, ITimeSeriesTable<T> table) : IMarketDataRepository<T>
{
    private readonly string _columnList = string.Join(", ", table.Columns);

    public async Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.ExecuteAsync(new CommandDefinition(table.CreateTableSql(tableName), cancellationToken: cancellationToken));
    }

    public async Task<DateTime?> GetLatestTimeAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        var latest = await connection.ExecuteScalarAsync<DateTime?>(
            new CommandDefinition($"SELECT max({table.TimeColumn}) FROM {tableName}", cancellationToken: cancellationToken));

        return latest is null ? null : Timestamps.FromTimestamp(latest.Value);
    }

    public async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition("SELECT to_regclass(@Name) IS NOT NULL", new { Name = tableName }, cancellationToken: cancellationToken));
    }

    public async Task<int> BulkInsert(string tableName, IEnumerable<T> rows, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var tempTableName = $"temp_series_{Guid.NewGuid():N}";

            await using (var createTemp = new NpgsqlCommand(
                $"CREATE TEMP TABLE {tempTableName} (LIKE {tableName}) ON COMMIT DROP;", connection, transaction))
            {
                await createTemp.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var writer = await connection.BeginBinaryImportAsync(
                $"COPY {tempTableName} ({_columnList}) FROM STDIN (FORMAT BINARY)", cancellationToken))
            {
                foreach (var row in rows)
                    await table.WriteRowAsync(writer, row, cancellationToken);

                await writer.CompleteAsync(cancellationToken);
            }

            await using var merge = new NpgsqlCommand($@"
                INSERT INTO {tableName} ({_columnList})
                SELECT {_columnList} FROM {tempTableName}
                ON CONFLICT DO NOTHING;", connection, transaction);

            var inserted = await merge.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return inserted;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }
}
