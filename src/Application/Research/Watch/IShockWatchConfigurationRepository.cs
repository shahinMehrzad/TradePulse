using Domain.Entities;

namespace Application.Research.Watch;

/// <summary>
/// The list of symbols the live watch follows (<c>config.shock_watch_configurations</c>). Written by the admin API, read by the watch.
/// Lives here, not with the repositories of the collector's settings, because the collector never reads it.
/// </summary>
public interface IShockWatchConfigurationRepository
{
    /// <summary>Every row with its listing (exchange, market, exchange name) loaded, ordered by the exchange's name.</summary>
    Task<IReadOnlyList<ShockWatchConfiguration>> GetAll(CancellationToken cancellationToken);

    Task<ShockWatchConfiguration?> GetById(int id, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Domain.Exceptions.ConfigurationConflictException"/> when the listing is on the list already, <see cref="Domain.Exceptions.ConfigurationValidationException"/> for an unknown listing.</summary>
    Task<int> Add(ShockWatchConfiguration configuration, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Domain.Exceptions.ConfigurationNotFoundException"/> when the row does not exist.</summary>
    Task SetEnabled(int id, bool enabled, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Domain.Exceptions.ConfigurationNotFoundException"/> when the row does not exist.</summary>
    Task Remove(int id, CancellationToken cancellationToken);
}
