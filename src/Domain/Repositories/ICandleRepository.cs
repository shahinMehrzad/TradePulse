using Domain.Entities;
using Domain.ValueObjects;

namespace Domain.Repositories
{
    public interface ICandleRepository
    {
        Task<IEnumerable<Candle>> GetHistoryAsync(string tableName, DateTime? from, DateTime? to, CancellationToken cancellationToken);

        /// <summary>
        /// Candles with <c>from &lt;= open_time &lt;= to</c> (both optional), oldest first, at most <paramref name="limit"/> rows.
        /// Throws <see cref="CandleSeriesNotFoundException"/> when the table does not exist.
        /// </summary>
        Task<IReadOnlyList<Candle>> GetPageAsync(string tableName, DateTime? from, DateTime? to, int limit, CancellationToken cancellationToken);

        /// <summary>
        /// The newest <paramref name="limit"/> candles with <c>open_time &lt;= upTo</c> (no upper bound when null),
        /// returned oldest first: the lookback window an indicator such as Ichimoku needs.
        /// Throws <see cref="CandleSeriesNotFoundException"/> when the table does not exist.
        /// </summary>
        Task<IReadOnlyList<Candle>> GetLatestAsync(string tableName, int limit, DateTime? upTo, CancellationToken cancellationToken);

        Task<int> BulkInsert(string tableName, IEnumerable<Candle> candles, CancellationToken cancellationToken);
        Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken);

        /// <summary>
        /// Returns the open time of the newest stored candle, or null when the table is empty.
        /// Database errors are propagated, never reported as "empty".
        /// </summary>
        Task<DateTimeOffset?> GetLatestOpenDataTime(string tableName, CancellationToken cancellationToken);

        /// <summary>
        /// Finds holes between stored candles. When <paramref name="expectedStart"/> is given and the first stored candle
        /// is at least one interval later, the missing run before it is reported as the first gap.
        /// </summary>
        Task<IReadOnlyList<CandleGap>> GetGapsAsync(string tableName, TimeSpan interval, DateTime? expectedStart, CancellationToken cancellationToken);
    }
}
