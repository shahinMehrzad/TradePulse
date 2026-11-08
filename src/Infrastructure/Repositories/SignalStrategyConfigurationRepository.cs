using Application.Signals;
using Domain.Entities;
using Domain.Exceptions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public class SignalStrategyConfigurationRepository(ConfigDbContext context) : ISignalStrategyConfigurationRepository
{
    // Reads are not tracked: the service asks for every signal it takes, and nothing may pile up in the tracker.
    public async Task<IReadOnlyList<SignalStrategyConfiguration>> GetAll(CancellationToken cancellationToken)
        => await context.SignalStrategyConfigurations.AsNoTracking().OrderBy(c => c.Strategy).ToListAsync(cancellationToken);

    public async Task<SignalStrategyConfiguration?> GetByStrategy(string strategy, CancellationToken cancellationToken)
        => await context.SignalStrategyConfigurations.AsNoTracking().SingleOrDefaultAsync(c => c.Strategy == strategy, cancellationToken);

    public async Task<int> Add(SignalStrategyConfiguration configuration, CancellationToken cancellationToken)
    {
        if (configuration.FindProblem() is { } problem)
            throw new ConfigurationValidationException(problem);

        context.SignalStrategyConfigurations.Add(configuration);
        await SaveAsync(cancellationToken);

        return configuration.Id;
    }

    public async Task Update(SignalStrategyConfiguration configuration, CancellationToken cancellationToken)
    {
        var entity = await context.SignalStrategyConfigurations.SingleOrDefaultAsync(c => c.Id == configuration.Id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Configuration does not exist.");

        if (entity.Strategy != configuration.Strategy)
            throw new ConfigurationValidationException("The name of a strategy cannot be changed: its signals and trades carry it.");

        entity.IsEnabled = configuration.IsEnabled;
        entity.MaxSignalAgeSeconds = configuration.MaxSignalAgeSeconds;
        entity.MaxMoveOfStopDistance = configuration.MaxMoveOfStopDistance;
        entity.MaxOpenPerSymbol = configuration.MaxOpenPerSymbol;
        entity.MaxLeverage = configuration.MaxLeverage;

        if (entity.FindProblem() is { } problem)
        {
            context.Entry(entity).State = EntityState.Detached;
            throw new ConfigurationValidationException(problem);
        }

        await SaveAsync(cancellationToken);
    }

    public async Task SetEnabled(int id, bool enabled, CancellationToken cancellationToken)
    {
        var entity = await context.SignalStrategyConfigurations.SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Configuration does not exist.");

        entity.IsEnabled = enabled;
        await SaveAsync(cancellationToken);
    }

    /// <summary>Saves, turning the database error a caller can cause into a domain exception. Anything else is a real failure and is left to propagate.</summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The unique index is the real guard; a check before saving can lose a race.
            throw new ConfigurationConflictException("This strategy has a row already.", ex);
        }
    }
}
