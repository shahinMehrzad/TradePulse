using Domain.Entities;
using Domain.Exceptions;
using Domain.ValueObjects;

namespace Domain.Repositories
{
    public interface ICandleRepository
    {
        Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken);

        Task<IReadOnlyList<Candle>> GetPageAsync(string tableName, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken);

        Task<IReadOnlyList<Candle>> GetLatestAsync(string tableName, int limit, DateTime? upTo, CancellationToken cancellationToken);

        Task<int> BulkInsert(string tableName, IEnumerable<Candle> candles, CancellationToken cancellationToken);
        Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken);

        Task<DateTimeOffset?> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken);

        
        Task<DateTimeOffset?> GetFirstOpenTimeAsync(string tableName, CancellationToken cancellationToken);

        Task<IReadOnlyList<CandleGap>> GetGapsAsync(string tableName, TimeSpan interval, DateTime? expectedStart, CancellationToken cancellationToken);
    }
}
