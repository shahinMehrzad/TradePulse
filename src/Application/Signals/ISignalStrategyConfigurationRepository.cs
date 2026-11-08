using Domain.Entities;

namespace Application.Signals;

/// <summary>
/// The strategies whose signals are taken, and their limits (<c>config.signal_strategy_configurations</c>). Written by the admin API, read by
/// the service that takes signals.
/// </summary>
public interface ISignalStrategyConfigurationRepository
{
    Task<IReadOnlyList<SignalStrategyConfiguration>> GetAll(CancellationToken cancellationToken);

    /// <summary>The row of a strategy, or <c>null</c> when there is none: its signals are refused.</summary>
    Task<SignalStrategyConfiguration?> GetByStrategy(string strategy, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Domain.Exceptions.ConfigurationValidationException"/> for values that are not allowed, <see cref="Domain.Exceptions.ConfigurationConflictException"/> when the strategy has a row already.</summary>
    Task<int> Add(SignalStrategyConfiguration configuration, CancellationToken cancellationToken);

    /// <summary>
    /// Changes the limits and whether the strategy is on. The name stays: a different one is a validation error, because the signals and trades
    /// already stored carry it. Throws <see cref="Domain.Exceptions.ConfigurationNotFoundException"/> when the row does not exist.
    /// </summary>
    Task Update(SignalStrategyConfiguration configuration, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Domain.Exceptions.ConfigurationNotFoundException"/> when the row does not exist.</summary>
    Task SetEnabled(int id, bool enabled, CancellationToken cancellationToken);
}
