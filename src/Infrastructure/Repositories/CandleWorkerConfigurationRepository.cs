using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Infrastructure.Repositories
{
    public class CandleWorkerConfigurationRepository(ConfigDbContext context) : ICandleWorkerConfigurationRepository
    {
        public async Task<int> AddNewCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken)
        {
            // StartDate is UTC (the caller normalises it). timestamp columns hold no zone and Npgsql rejects Kind=Utc for them.
            configuration.StartDate = DateTime.SpecifyKind(configuration.StartDate, DateTimeKind.Unspecified);

            configuration.SymbolListing = await FindListingAsync(configuration.SymbolListingId, cancellationToken);
            context.CandleWorkerConfigurations.Add(configuration);
            await SaveAsync(cancellationToken);

            return configuration.Id;
        }

        public async Task RemoveCandleWorkerConfiguration(int configurationId, CancellationToken cancellationToken)
        {
            var entity = await context.CandleWorkerConfigurations.SingleOrDefaultAsync(c => c.Id == configurationId, cancellationToken)
                ?? throw new ConfigurationNotFoundException("Configuration does not exist.");
            context.CandleWorkerConfigurations.Remove(entity);
            await SaveAsync(cancellationToken);
        }

        public async Task<CandleWorkerConfiguration?> GetConfigurationById(int id, CancellationToken cancellationToken)
            => await context.CandleWorkerConfigurations.AsNoTracking().Include(c => c.SymbolListing).SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

        public async Task<CandleWorkerConfiguration?> GetConfiguration(int symbolListingId, int timeFrameId, CancellationToken cancellationToken)
            => await context.CandleWorkerConfigurations.AsNoTracking().Include(c => c.SymbolListing)
                .SingleOrDefaultAsync(c => c.SymbolListingId == symbolListingId && c.TimeFrameId == timeFrameId, cancellationToken);


        public async Task<CandleWorkerConfiguration> UpdateCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken)
        {
            var config = await context.CandleWorkerConfigurations.SingleOrDefaultAsync(c => c.Id == configuration.Id, cancellationToken)
                ?? throw new ConfigurationNotFoundException("Configuration does not exist.");

            config.SymbolListing = await FindListingAsync(configuration.SymbolListingId, cancellationToken);
            config.TimeFrameId = configuration.TimeFrameId;
            config.StartDate = DateTime.SpecifyKind(configuration.StartDate, DateTimeKind.Unspecified);
            config.CollectOpenInterest = configuration.CollectOpenInterest;
            config.CollectMarketStatistics = configuration.CollectMarketStatistics;
            await SaveAsync(cancellationToken);

            return config;
        }

        /// <summary>The listing a configuration points at, tracked so that it is not added again; a missing one is the caller's mistake.</summary>
        private async Task<SymbolListing> FindListingAsync(int symbolListingId, CancellationToken cancellationToken)
            => await context.SymbolListings.SingleOrDefaultAsync(l => l.Id == symbolListingId, cancellationToken)
                ?? throw new ConfigurationValidationException("Unknown symbol listing.");

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
                throw new ConfigurationValidationException("Unknown symbol listing or time frame.");
            }
        }

        public async Task<IReadOnlyList<CandleWorkerConfiguration>> GetConfigurationList(CancellationToken cancellationToken)
            => await context.CandleWorkerConfigurations.Include(c => c.SymbolListing).OrderBy(c => c.SymbolListing!.ExchangeSymbol)
                .ThenBy(c => c.Id).AsNoTracking().ToListAsync(cancellationToken);

    }
}
