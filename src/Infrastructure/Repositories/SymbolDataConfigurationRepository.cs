using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories;

public class SymbolDataConfigurationRepository(ConfigDbContext context) : ISymbolDataConfigurationRepository
{
    public async Task<IReadOnlyList<SymbolDataConfiguration>> GetAll(CancellationToken cancellationToken)
        => await context.SymbolDataConfigurations.AsNoTracking().Include(c => c.SymbolListing)
            .OrderBy(c => c.SymbolListing!.ExchangeSymbol).ThenBy(c => c.Id)
            .ToListAsync(cancellationToken);

    public async Task<SymbolDataConfiguration?> GetById(int id, CancellationToken cancellationToken)
        => await context.SymbolDataConfigurations.AsNoTracking().Include(c => c.SymbolListing).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

    public async Task<int> Add(SymbolDataConfiguration configuration, CancellationToken cancellationToken)
    {
        configuration.FundingStartDate = ToTimestamp(configuration.FundingStartDate);

        configuration.SymbolListing = await FindListingAsync(configuration.SymbolListingId, cancellationToken);
        context.SymbolDataConfigurations.Add(configuration);
        await SaveAsync(cancellationToken);

        return configuration.Id;
    }

    public async Task<SymbolDataConfiguration> Update(SymbolDataConfiguration configuration, CancellationToken cancellationToken)
    {
        var config = await context.SymbolDataConfigurations.SingleOrDefaultAsync(c => c.Id == configuration.Id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Configuration does not exist.");

        config.SymbolListing = await FindListingAsync(configuration.SymbolListingId, cancellationToken);
        config.CollectFundingRate = configuration.CollectFundingRate;
        config.FundingStartDate = ToTimestamp(configuration.FundingStartDate);
        config.CollectLiquidation = configuration.CollectLiquidation;
        await SaveAsync(cancellationToken);

        return config;
    }

    public async Task Remove(int id, CancellationToken cancellationToken)
    {
        var entity = await context.SymbolDataConfigurations.SingleOrDefaultAsync(c => c.Id == id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Configuration does not exist.");
        context.SymbolDataConfigurations.Remove(entity);
        await SaveAsync(cancellationToken);
    }

    /// <summary>The listing a configuration points at, tracked so that it is not added again; a missing one is the caller's mistake.</summary>
    private async Task<SymbolListing> FindListingAsync(int symbolListingId, CancellationToken cancellationToken)
        => await context.SymbolListings.SingleOrDefaultAsync(l => l.Id == symbolListingId, cancellationToken)
            ?? throw new ConfigurationValidationException("Unknown exchange, market or symbol.");

    // The date is UTC (the caller normalises it). timestamp columns hold no zone and Npgsql rejects Kind=Utc for them.
    private static DateTime? ToTimestamp(DateTime? utc) => utc is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Unspecified) : null;

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
            throw new ConfigurationConflictException("Configuration already exists.", ex);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            throw new ConfigurationValidationException("Unknown exchange, market or symbol.");
        }
    }
}
