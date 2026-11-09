using Domain.ValueObjects;

namespace Domain.Repositories;

/// <summary>Where the collector writes down the holes the exchange itself does not have (see <see cref="Domain.Entities.ConfirmedCandleGap"/>).</summary>
public interface IConfirmedGapStore
{
    /// <summary>Adds the hole; the same hole written twice is one row. Throws on a database error: the caller decides that it only logs.</summary>
    Task ConfirmAsync(string tableName, CandleGap gap, int asks, CancellationToken cancellationToken);
}
