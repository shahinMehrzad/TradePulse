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

        /// <summary>
        /// The holes between the candles from <paramref name="since"/> on, and only those: the scan reaches the newest rows through the primary key,
        /// so it costs the same for a table of years as for a table of days. A hole at the very start of the stretch is found too (the candle just
        /// before <paramref name="since"/> is the other end of it). The run in front of the first candle is not looked at: that is the full scan's.
        /// </summary>
        Task<IReadOnlyList<CandleGap>> GetRecentGapsAsync(string tableName, TimeSpan interval, DateTime since, CancellationToken cancellationToken);
    }
}
