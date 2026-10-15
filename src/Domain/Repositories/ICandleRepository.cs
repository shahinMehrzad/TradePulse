using Domain.Entities;
using Domain.ValueObjects;

namespace Domain.Repositories
{
    public interface ICandleRepository
    {
        Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken);
        Task<int> BulkInsert(string tableName, IEnumerable<Candle> candles, CancellationToken cancellationToken);
        Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken);

        /// <summary>
        /// Returns the open time of the newest stored candle, or null when the table is empty.
        /// Database errors are propagated, never reported as "empty".
        /// </summary>
        Task<DateTimeOffset?> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken);

        /// <summary>
        /// Finds holes between stored candles (missing candles before the first row are not detected).
        /// </summary>
        Task<IReadOnlyList<CandleGap>> GetGapsAsync(string tableName, TimeSpan interval, CancellationToken cancellationToken);
    }
}
