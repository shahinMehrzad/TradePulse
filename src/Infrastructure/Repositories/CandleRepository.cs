using Dapper;
using Domain.Entities;
using Domain.Repositories;
using Domain.ValueObjects;
using Npgsql;
using static Infrastructure.Repositories.TimeSeries.Timestamps;

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

            await using (var tempTableCommand = new NpgsqlCommand(
            $"CREATE TEMP TABLE {tempTableName} (LIKE {tableName}) ON COMMIT DROP;", connection, transaction))
            {
                await tempTableCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var writer = await connection.BeginBinaryImportAsync(
        $"COPY {tempTableName} (open_time, close_time, open_price, high_price, low_price, close_price, volume, quote_asset_volume, number_of_trades, taker_buy_base_volume, taker_buy_quote_volume) FROM STDIN (FORMAT BINARY)",
            cancellationToken))
            {
                foreach (var candle in candles)
                {
                    await writer.StartRowAsync(cancellationToken);
                    await writer.WriteAsync(ToTimestamp(candle.TimeRange.OpenTime), NpgsqlTypes.NpgsqlDbType.Timestamp, cancellationToken);
                    await writer.WriteAsync(ToTimestamp(candle.TimeRange.CloseTime), NpgsqlTypes.NpgsqlDbType.Timestamp, cancellationToken);
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

            await using var mergeCommand = new NpgsqlCommand($@"
            INSERT INTO {tableName} (open_time, close_time, open_price, high_price, low_price, close_price, volume, quote_asset_volume, number_of_trades, taker_buy_base_volume, taker_buy_quote_volume)
            SELECT open_time, close_time, open_price, high_price, low_price, close_price, volume, quote_asset_volume, number_of_trades, taker_buy_base_volume, taker_buy_quote_volume
            FROM {tempTableName}
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
<<<<<<< Updated upstream
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
=======
        var (where, parameters) = TimeFilter(from, to);
>>>>>>> Stashed changes

        return await QueryCandlesAsync(tableName, $"{SelectCandles(tableName)}{where} ORDER BY open_time ASC;", parameters, cancellationToken);
    }

    public async Task<IReadOnlyList<Candle>> GetPageAsync(string tableName, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken)
    {
        var (where, parameters) = TimeFilter(from, to);
        parameters.Add("Limit", limit);

        return await QueryCandlesAsync(tableName, $"{SelectCandles(tableName)}{where} ORDER BY open_time ASC LIMIT @Limit;", parameters, cancellationToken);
    }

    public async Task<IReadOnlyList<Candle>> GetLatestAsync(string tableName, int limit, DateTime? upTo, CancellationToken cancellationToken)
    {
        var (where, parameters) = TimeFilter(null, upTo);
        parameters.Add("Limit", limit);

        // Newest-first so the primary key is walked backwards and stops after `limit` rows; flipped to ascending below.
        var candles = await QueryCandlesAsync(tableName, $"{SelectCandles(tableName)}{where} ORDER BY open_time DESC LIMIT @Limit;", parameters, cancellationToken);
        candles.Reverse();
        return candles;
    }

    // Only the bounds that are present become predicates: a null parameter has no type for Postgres to infer,
    // and a plain range predicate keeps the primary key usable.
    private static (string Where, DynamicParameters Parameters) TimeFilter(DateTime? from, DateTime? to)
    {
        var parameters = new DynamicParameters();
        var conditions = new List<string>(2);

<<<<<<< Updated upstream
        var candles = await connection.QueryAsync<Candle>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));

        return candles;
=======
        if (from is { } f)
        {
            conditions.Add("open_time >= @From");
            parameters.Add("From", ToTimestamp(f));
        }
        if (to is { } t)
        {
            conditions.Add("open_time <= @To");
            parameters.Add("To", ToTimestamp(t));
        }

        return (conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions), parameters);
    }

    private static string SelectCandles(string tableName) => $@"
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
        FROM {tableName}";

    private async Task<List<Candle>> QueryCandlesAsync(string tableName, string sql, DynamicParameters parameters, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);

        IEnumerable<CandleRow> rows;
        try
        {
            // Dapper cannot fill Candle.TimeRange (a struct) from flat columns, so map through a row type.
            rows = await connection.QueryAsync<CandleRow>(new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            throw new CandleSeriesNotFoundException(tableName, ex);
        }

        return rows.Select(r => new Candle(
            FromTimestamp(r.OpenTime), r.Open, r.High, r.Low, r.Close, r.Volume,
            FromTimestamp(r.CloseTime), r.QuoteAssetVolume, r.NumberOfTrades, r.TakerBuyBaseVolume, r.TakerBuyQuoteVolume)).ToList();
>>>>>>> Stashed changes
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

    public async Task<DateTimeOffset?> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var latest = await connection.ExecuteScalarAsync<DateTime?>(
            new CommandDefinition($"SELECT max(open_time) FROM {tableName}", cancellationToken: cancellationToken));

        return latest is null ? null : new DateTimeOffset(FromTimestamp(latest.Value));
    }

    public async Task<IReadOnlyList<CandleGap>> GetGapsAsync(string tableName, TimeSpan interval, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        var sql = $@"
            SELECT prev_open AS PrevOpen, open_time AS NextOpen
            FROM (
                SELECT open_time, LAG(open_time) OVER (ORDER BY open_time) AS prev_open
                FROM {tableName}
            ) t
            WHERE open_time - prev_open > @IntervalSeconds * interval '1 second'
            ORDER BY open_time;";

        var rows = await connection.QueryAsync<GapRow>(
            new CommandDefinition(sql, new { IntervalSeconds = (int)interval.TotalSeconds }, cancellationToken: cancellationToken));

        return rows
            .Select(r => new CandleGap(
                FromTimestamp(r.PrevOpen + interval),
                FromTimestamp(r.NextOpen - interval)))
            .ToList();
    }

    private sealed record GapRow(DateTime PrevOpen, DateTime NextOpen);
}
