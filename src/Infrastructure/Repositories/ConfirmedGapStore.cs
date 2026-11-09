using Dapper;
using Domain.Repositories;
using Domain.ValueObjects;
using Npgsql;
using static Infrastructure.Helpers.Timestamps;

namespace Infrastructure.Repositories;

public class ConfirmedGapStore(string connectionString) : IConfirmedGapStore
{
    public async Task ConfirmAsync(string tableName, CandleGap gap, int asks, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // The table name is a value here, not part of the SQL. The unique key makes a repeat (a restart that asked again) a no-op.
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO config.candle_confirmed_gaps ("TableName", "FirstOpenTime", "LastOpenTime", "Asks")
            VALUES (@TableName, @First, @Last, @Asks)
            ON CONFLICT ("TableName", "FirstOpenTime", "LastOpenTime") DO NOTHING
            """,
            new
            {
                TableName = tableName,
                First = ToTimestamp(gap.FirstMissingOpenTime),
                Last = ToTimestamp(gap.LastMissingOpenTime),
                Asks = asks,
            },
            cancellationToken: cancellationToken));
    }
}
