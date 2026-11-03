using Application.Research.Watch;
using Domain.Entities;
using Domain.Exceptions;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public class ShockWatchConfigurationRepository(ConfigDbContext context) : IShockWatchConfigurationRepository
{
    // Reads are not tracked: the watch asks every 30 seconds for as long as it runs, and nothing may pile up in the tracker.
    public async Task<IReadOnlyList<ShockWatchConfiguration>> GetAll(CancellationToken cancellationToken)
        => await context.ShockWatchConfigurations.AsNoTracking().Include(c => c.SymbolListing)
            .OrderBy(c => c.SymbolListing!.ExchangeSymbol).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

    public async Task<ShockWatchConfiguration?> GetById(int id, CancellationToken cancellationToken)
        => await context.ShockWatchConfigurations.AsNoTracking().Include(c => c.SymbolListing).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<int> Add(ShockWatchConfiguration configuration, CancellationToken cancellationToken)
    {
        // The listing is tracked, so that it is not added again; a missing one is the caller's mistake.
        configuration.SymbolListing = await context.SymbolListings.SingleOrDefaultAsync(l => l.Id == configuration.SymbolListingId, cancellationToken)
            ?? throw new ConfigurationValidationException("Unknown symbol listing.");

        context.ShockWatchConfigurations.Add(configuration);
        await SaveAsync(cancellationToken);

        return configuration.Id;
    }

    public async Task SetEnabled(int id, bool enabled, CancellationToken cancellationToken)
    {
        var entity = await context.ShockWatchConfigurations.SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Configuration does not exist.");

        entity.IsEnabled = enabled;
        await SaveAsync(cancellationToken);
    }

    public async Task Remove(int id, CancellationToken cancellationToken)
    {
        var entity = await context.ShockWatchConfigurations.SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Configuration does not exist.");

        context.ShockWatchConfigurations.Remove(entity);
        await SaveAsync(cancellationToken);
    }

    /// <summary>Saves, turning the two database errors a caller can cause into domain exceptions. Anything else is a real failure and is left to propagate.</summary>
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The unique index is the real guard; a check before saving can lose a race.
            throw new ConfigurationConflictException("This symbol is on the watch list already.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            throw new ConfigurationValidationException("Unknown symbol listing.");
        }
    }
}
