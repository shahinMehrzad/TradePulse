using Domain.Entities;
using Domain.Repositories;
using Domain.ValueObjects;

namespace Application.UnitTests;

internal sealed class FakeCandleRepository : ICandleRepository
{
    public List<(string TableName, List<Candle> Candles)> Inserts { get; } = [];
    public int InsertResult { get; set; }

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

    public Task<IReadOnlyList<CandleGap>> GetGapsAsync(string tableName, TimeSpan interval, DateTime? expectedStart, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
