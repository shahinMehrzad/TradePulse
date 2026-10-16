using Domain.Entities;
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

    public Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<DateTimeOffset?> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<Candle>> GetPageAsync(string tableName, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken)
    {
        LastRead = (tableName, from, to, limit);
        return Task.FromResult<IReadOnlyList<Candle>>(Stored.Take(limit).ToList());
    }

    public Task<IReadOnlyList<Candle>> GetLatestAsync(string tableName, int limit, DateTime? upTo, CancellationToken cancellationToken)
    {
        LastRead = (tableName, null, upTo, limit);
        return Task.FromResult<IReadOnlyList<Candle>>(Stored.TakeLast(limit).ToList());
    }

    public Task<IReadOnlyList<CandleGap>> GetGapsAsync(string tableName, TimeSpan interval, DateTime? expectedStart, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
