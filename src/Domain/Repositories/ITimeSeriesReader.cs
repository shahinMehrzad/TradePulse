namespace Domain.Repositories;

/// <summary>
/// Reads the newest rows of one kind of time series for display (the dashboard). Only what the writer's <see cref="IMarketDataRepository{T}"/>
/// does not need to know: the collector never reads rows back, so the two stay apart and the collector path is untouched.
/// <c>tableName</c> comes from <c>TableNameRegistry</c>, never from the caller.
/// </summary>
public interface ITimeSeriesReader<T>
{
    /// <summary>The newest <paramref name="count"/> rows (fewer when the table holds fewer), oldest first. A missing table is an error: ask <see cref="IMarketDataRepository{T}.TableExistsAsync"/> first.</summary>
    Task<IReadOnlyList<T>> GetNewestAsync(string tableName, int count, CancellationToken cancellationToken);
}
