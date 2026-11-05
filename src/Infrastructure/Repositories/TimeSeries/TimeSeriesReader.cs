using Domain.Repositories;
using Infrastructure.Helpers;
using Npgsql;

namespace Infrastructure.Repositories.TimeSeries;

/// <summary>A table that can also be read back: the same column order <see cref="ITimeSeriesTable{T}"/> writes, the time first.</summary>
public interface IReadableTimeSeriesTable<out T>
{
    /// <summary>Column used for ordering ("newest rows").</summary>
    string TimeColumn { get; }

    /// <summary>Columns in the order <see cref="ReadRow"/> reads them; the first is the time.</summary>
    IReadOnlyList<string> Columns { get; }

    T ReadRow(NpgsqlDataReader reader);
}

/// <summary>Reads the newest rows of a non-candle time series, for the dashboard.</summary>
public sealed class TimeSeriesReader<T>(string connectionString, IReadableTimeSeriesTable<T> table) : ITimeSeriesReader<T>
{
    private readonly string _columnList = string.Join(", ", table.Columns);

    /// <summary>The table's name goes into the SQL (it comes from <c>TableNameRegistry</c>); the count is a parameter.</summary>
    public async Task<IReadOnlyList<T>> GetNewestAsync(string tableName, int count, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = new NpgsqlCommand(
            $"SELECT {_columnList} FROM {tableName} ORDER BY {table.TimeColumn} DESC LIMIT @Count", connection);
        command.Parameters.AddWithValue("Count", Math.Max(0, count));

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add(table.ReadRow(reader));

        rows.Reverse();
        return rows;
    }
}
