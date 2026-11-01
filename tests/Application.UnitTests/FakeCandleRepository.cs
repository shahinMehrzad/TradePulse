using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;
using Domain.ValueObjects;

namespace Application.UnitTests;

internal sealed class FakeCandleRepository : ICandleRepository
{
    public List<(string TableName, List<Candle> Candles)> Inserts { get; } = [];
    public int InsertResult { get; set; }

    /// <summary>What the read methods return, and the arguments of the last read.</summary>
    public List<Candle> Stored { get; } = [];
    public (string TableName, DateTime? From, DateTime? To, int Limit)? LastRead { get; private set; }

    public Task<int> BulkInsert(string tableName, IEnumerable<Candle> candles, CancellationToken cancellationToken)
    {
        Inserts.Add((tableName, candles.ToList()));
        return Task.FromResult(InsertResult);
    }

    public Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken)
    {
        LastRead = (tableName, from, to, 0);
        return Task.FromResult<IEnumerable<Candle>>(Stored
            .Where(c => (from is null || c.TimeRange.OpenTime >= from) && (to is null || c.TimeRange.OpenTime <= to))
            .ToList());
    }

    public Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<DateTimeOffset?> GetFirstOpenTimeAsync(string tableName, CancellationToken cancellationToken) => throw new NotSupportedException();
    public Task<DateTimeOffset?> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Candle>> GetPageAsync(string tableName, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken)
    {
        LastRead = (tableName, from, to, limit);
        return Task.FromResult<IReadOnlyList<Candle>>(Stored.Take(limit).ToList());
    }

    /// <summary>
    /// When used, each table has its own candles and a table not listed does not exist (like a real database). Lets one test
    /// cover several series; <see cref="Stored"/> is ignored by <see cref="GetLatestAsync"/> then.
    /// </summary>
    public Dictionary<string, List<Candle>> PerTable { get; } = [];

    public Task<IReadOnlyList<Candle>> GetLatestAsync(string tableName, int limit, DateTime? upTo, CancellationToken cancellationToken)
    {
        LastRead = (tableName, null, upTo, limit);

        if (PerTable.Count == 0)
            return Task.FromResult<IReadOnlyList<Candle>>(Stored.TakeLast(limit).ToList());

        return PerTable.TryGetValue(tableName, out var candles)
            ? Task.FromResult<IReadOnlyList<Candle>>(candles.TakeLast(limit).ToList())
            : throw new CandleSeriesNotFoundException(tableName);
    }

    public Task<IReadOnlyList<CandleGap>> GetGapsAsync(string tableName, TimeSpan interval, DateTime? expectedStart, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
