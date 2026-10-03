using Dapper;
using Domain.Entities;
using Domain.Repositories;
using Npgsql;
using System.Data;

namespace Infrastructure.Repositories;

public class CandleRepository(string connectionString) : ICandleRepository
{
    private readonly string _connectionString = connectionString;

    public async Task<int> BulkInsert(string tableName, IEnumerable<Candle> candles, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var tempTableName = $"temp_candles_{Guid.NewGuid():N}";
            using (var tempTableCommand = new NpgsqlCommand($"CREATE TEMP TABLE {tempTableName} (LIKE {tableName} INCLUDING ALL) ON COMMIT DROP;", connection, transaction))
            {
                await tempTableCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            {
                await using var writer = await connection.BeginBinaryImportAsync(
                    $"COPY {tableName} (open_time, close_time, open_price, high_price, low_price, close_price, volume, quote_asset_volume, number_of_trades, taker_buy_base_volume, taker_buy_quote_volume) FROM STDIN (FORMAT BINARY)",
                    cancellationToken);

                foreach (var candle in candles)
                {
                    await writer.StartRowAsync(cancellationToken);

                    await writer.WriteAsync(DateTime.SpecifyKind(candle.TimeRange.OpenTime, DateTimeKind.Utc), NpgsqlTypes.NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(DateTime.SpecifyKind(candle.TimeRange.CloseTime, DateTimeKind.Utc), NpgsqlTypes.NpgsqlDbType.TimestampTz, cancellationToken);
                    await writer.WriteAsync(candle.Open.Value, NpgsqlTypes.NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(candle.High.Value, NpgsqlTypes.NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(candle.Low.Value, NpgsqlTypes.NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(candle.Close.Value, NpgsqlTypes.NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(candle.Volume, NpgsqlTypes.NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(candle.QuoteAssetVolume, NpgsqlTypes.NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(candle.NumberOfTrades, NpgsqlTypes.NpgsqlDbType.Bigint, cancellationToken);
                    await writer.WriteAsync(candle.TakerBuyBaseVolume, NpgsqlTypes.NpgsqlDbType.Numeric, cancellationToken);
                    await writer.WriteAsync(candle.TakerBuyQuoteVolume, NpgsqlTypes.NpgsqlDbType.Numeric, cancellationToken);
                }

                await writer.CompleteAsync(cancellationToken);
            }

            var mergeCommand = new NpgsqlCommand($@" INSERT INTO {tableName} 
                SELECT * FROM {tempTableName}
                ON CONFLICT (open_time) DO NOTHING;", connection, transaction);

            int insertedCount = await mergeCommand.ExecuteNonQueryAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return insertedCount;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    public async Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        using var connection = new NpgsqlConnection(_connectionString);
        var sql = $@"
                SELECT 
                    open_time AS OpenTime,
                    open_price AS Open,
                    high_price AS High,
                    low_price AS Low,
                    close_price AS Close,
                    volume AS Volume,
                    close_time AS CloseTime,
                    quote_asset_volume AS QuoteAssetVolume,
                    number_of_trades AS NumberOfTrades,
                    taker_buy_base_volume AS TakerBuyBaseVolume,
                    taker_buy_quote_volume AS TakerBuyQuoteVolume
                FROM {tableName} WHERE 1 = 1 ";

        var parameters = new DynamicParameters();
        if (from.HasValue)
        {
            sql += " AND open_time >= @From ";
            parameters.Add("From", from.Value);
        }
        if (to.HasValue)
        {
            sql += " AND open_time <= @To ";
            parameters.Add("To", to.Value);
        }
        sql += " ORDER BY open_time ASC;";

        var candles = await connection.QueryAsync<Candle>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));

        return candles;
    }

    public async Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        var createTableSql = $@"
            CREATE TABLE IF NOT EXISTS {tableName} (               
                open_time timestamp NOT NULL,
                open_price NUMERIC(18, 8) NOT NULL,
                high_price NUMERIC(18, 8) NOT NULL,
                low_price NUMERIC(18, 8) NOT NULL,
                close_price NUMERIC(18, 8) NOT NULL,
                volume NUMERIC(28, 8) NOT NULL,
                close_time timestamp NOT NULL,
                quote_asset_volume NUMERIC(28, 8) NOT NULL,
                number_of_trades BIGINT NOT NULL,
                taker_buy_base_volume NUMERIC(28, 8) NOT NULL,
                taker_buy_quote_volume NUMERIC(28, 8) NOT NULL,

                CONSTRAINT pk_{tableName} PRIMARY KEY (open_time)                
            );
        ";

        await connection.ExecuteAsync(createTableSql);
    }

    public async Task<DateTimeOffset> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken)
    {
        try
        {
            using var connection = new NpgsqlConnection(_connectionString);
            var sql = $@"SELECT open_time AS CloseTime  FROM {tableName} order by CloseTime desc  limit 1";
            return DateTime.SpecifyKind(await connection.QueryFirstAsync<DateTime>(sql), DateTimeKind.Utc);
            
        }
        catch (Exception)
        {
            return DateTimeOffset.MinValue;
        }
    }
}
