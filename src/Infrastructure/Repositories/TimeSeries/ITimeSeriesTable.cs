using Npgsql;

namespace Infrastructure.Repositories.TimeSeries;

/// <summary>Describes how one record type maps onto its Postgres table.</summary>
public interface ITimeSeriesTable<in T>
{
    /// <summary>Column used for resuming ("newest stored row").</summary>
    string TimeColumn { get; }

    /// <summary>Columns in the order <see cref="WriteRowAsync"/> writes them.</summary>
    IReadOnlyList<string> Columns { get; }

    string CreateTableSql(string tableName);

    Task WriteRowAsync(NpgsqlBinaryImporter writer, T row, CancellationToken cancellationToken);
}
