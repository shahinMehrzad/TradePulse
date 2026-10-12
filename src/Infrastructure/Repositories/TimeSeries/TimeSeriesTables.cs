using Domain.Entities;
using Npgsql;
using NpgsqlTypes;

namespace Infrastructure.Repositories.TimeSeries;

public sealed class FundingRateTable : ITimeSeriesTable<FundingRate>
{
    public string TimeColumn => "funding_time";

    public IReadOnlyList<string> Columns { get; } = ["funding_time", "funding_rate", "mark_price"];

    public string CreateTableSql(string tableName) => $@"
        CREATE TABLE IF NOT EXISTS {tableName} (
            funding_time timestamp NOT NULL,
            funding_rate NUMERIC(18, 8) NOT NULL,
            mark_price NUMERIC(18, 8) NOT NULL,

            CONSTRAINT pk_{tableName} PRIMARY KEY (funding_time)
        );";

    public async Task WriteRowAsync(NpgsqlBinaryImporter writer, FundingRate row, CancellationToken cancellationToken)
    {
        await writer.StartRowAsync(cancellationToken);
        await writer.WriteAsync(Timestamps.ToTimestamp(row.FundingTime), NpgsqlDbType.Timestamp, cancellationToken);
        await writer.WriteAsync(row.Rate, NpgsqlDbType.Numeric, cancellationToken);
        await writer.WriteAsync(row.MarkPrice, NpgsqlDbType.Numeric, cancellationToken);
    }
}

public sealed class OpenInterestTable : ITimeSeriesTable<OpenInterest>
{
    public string TimeColumn => "snapshot_time";

    public IReadOnlyList<string> Columns { get; } = ["snapshot_time", "sum_open_interest", "sum_open_interest_value"];

    public string CreateTableSql(string tableName) => $@"
        CREATE TABLE IF NOT EXISTS {tableName} (
            snapshot_time timestamp NOT NULL,
            sum_open_interest NUMERIC(28, 8) NOT NULL,
            sum_open_interest_value NUMERIC(28, 8) NOT NULL,

            CONSTRAINT pk_{tableName} PRIMARY KEY (snapshot_time)
        );";

    public async Task WriteRowAsync(NpgsqlBinaryImporter writer, OpenInterest row, CancellationToken cancellationToken)
    {
        await writer.StartRowAsync(cancellationToken);
        await writer.WriteAsync(Timestamps.ToTimestamp(row.Time), NpgsqlDbType.Timestamp, cancellationToken);
        await writer.WriteAsync(row.SumOpenInterest, NpgsqlDbType.Numeric, cancellationToken);
        await writer.WriteAsync(row.SumOpenInterestValue, NpgsqlDbType.Numeric, cancellationToken);
    }
}

public sealed class LiquidationTable : ITimeSeriesTable<Liquidation>
{
    public string TimeColumn => "event_time";

    public IReadOnlyList<string> Columns { get; } =
    [
        "event_time", "side", "order_type", "time_in_force", "status",
        "quantity", "price", "average_price", "last_filled_quantity", "accumulated_quantity"
    ];

    // No primary key: several liquidations can share a millisecond and the stream carries no unique id.
    // Duplicates cannot occur because the stream is never replayed.
    public string CreateTableSql(string tableName) => $@"
        CREATE TABLE IF NOT EXISTS {tableName} (
            event_time timestamp NOT NULL,
            side text NOT NULL,
            order_type text NOT NULL,
            time_in_force text NOT NULL,
            status text NOT NULL,
            quantity NUMERIC(28, 8) NOT NULL,
            price NUMERIC(18, 8) NOT NULL,
            average_price NUMERIC(18, 8) NOT NULL,
            last_filled_quantity NUMERIC(28, 8) NOT NULL,
            accumulated_quantity NUMERIC(28, 8) NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_{tableName}_event_time ON {tableName} (event_time);";

    public async Task WriteRowAsync(NpgsqlBinaryImporter writer, Liquidation row, CancellationToken cancellationToken)
    {
        await writer.StartRowAsync(cancellationToken);
        await writer.WriteAsync(Timestamps.ToTimestamp(row.Time), NpgsqlDbType.Timestamp, cancellationToken);
        await writer.WriteAsync(row.Side.ToString(), NpgsqlDbType.Text, cancellationToken);
        await writer.WriteAsync(row.OrderType, NpgsqlDbType.Text, cancellationToken);
        await writer.WriteAsync(row.TimeInForce, NpgsqlDbType.Text, cancellationToken);
        await writer.WriteAsync(row.Status, NpgsqlDbType.Text, cancellationToken);
        await writer.WriteAsync(row.Quantity, NpgsqlDbType.Numeric, cancellationToken);
        await writer.WriteAsync(row.Price, NpgsqlDbType.Numeric, cancellationToken);
        await writer.WriteAsync(row.AveragePrice, NpgsqlDbType.Numeric, cancellationToken);
        await writer.WriteAsync(row.LastFilledQuantity, NpgsqlDbType.Numeric, cancellationToken);
        await writer.WriteAsync(row.AccumulatedQuantity, NpgsqlDbType.Numeric, cancellationToken);
    }
}
