using Microsoft.Extensions.Logging;

namespace BackgroundWorker.Collection;

internal static class TableCreation
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Runs <paramref name="ensure"/> (create a table if it is missing) until it succeeds. A database error is retried, not
    /// thrown: an exception out of a worker would stop the whole host. Only shutdown ends the wait early.
    /// </summary>
    public static async Task EnsureWithRetryAsync(Func<CancellationToken, Task> ensure, string tableName, ILogger logger, CancellationToken ct)
    {
        while (true)
        {
            try
            {
                await ensure(ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogError(ex, "Cannot create table {Table}; retrying in {Delay}", tableName, RetryDelay);
                await Task.Delay(RetryDelay, ct);
            }
        }
    }
}
