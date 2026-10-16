namespace Domain.Repositories;

/// <summary>The candle table for a requested series does not exist, i.e. the collector never created it.</summary>
public sealed class CandleSeriesNotFoundException(string tableName, Exception? inner = null)
    : Exception($"No candle data exists for '{tableName}'.", inner)
{
    public string TableName { get; } = tableName;
}
