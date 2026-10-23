namespace Domain.Repositories
{
    /// <summary>
    /// Storage for one kind of time series (funding rates, open interest, liquidations, ...).
    /// Each exchange/symbol(/timeframe) lives in its own table, named by <c>TableNameRegistry</c>.
    /// </summary>
    public interface IMarketDataRepository<T>
    {
        Task EnsureTableExistsAsync(string tableName, CancellationToken cancellationToken);

        /// <summary>Newest stored time (UTC), or null when the table is empty. Database errors are propagated.</summary>
        Task<DateTime?> GetLatestTimeAsync(string tableName, CancellationToken cancellationToken);

        /// <summary>Whether the table exists yet. The collector creates it when it first knows it should collect the data.</summary>
        Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken);

        /// <summary>Inserts the rows and skips duplicates. Returns the number of newly inserted rows.</summary>
        Task<int> BulkInsert(string tableName, IEnumerable<T> rows, CancellationToken cancellationToken);
    }
}
