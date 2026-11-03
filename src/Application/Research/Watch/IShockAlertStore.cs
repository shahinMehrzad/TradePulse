using Domain.Entities;

namespace Application.Research.Watch;

/// <summary>Where the shock alerts are kept (the research schema, like <see cref="IMarketEventStore"/>).</summary>
public interface IShockAlertStore
{
    /// <summary>
    /// Stores the alert, or updates the stored one when listing, rule version and start are the same: an alert is saved when it
    /// starts and again when it ends, with its final level and largest move.
    /// </summary>
    Task SaveAsync(ShockAlert alert, CancellationToken cancellationToken);
}
