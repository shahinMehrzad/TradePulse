using Domain.Entities;

namespace Domain.Repositories
{
    public interface ICandleRepository
    {
        Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken);
        Task<int> BulkInsert(string tableName, IEnumerable<Candle> candles, CancellationToken cancellationToken);
        Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken);
        Task<DateTimeOffset> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken);
    }
}
