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

            context.CandleWorkerConfigurations.Add(configuration);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                throw new ConfigurationConflictException("Configuration already exists.", ex);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
            {
                throw new ConfigurationValidationException("Unknown exchange, market, symbol or time frame.");
            }

            return configuration.Id;
        }

        public async Task<CandleWorkerConfiguration?> GetConfiguration(int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, CancellationToken cancellationToken)
            => await context.CandleWorkerConfigurations.AsNoTracking().SingleOrDefaultAsync(c => c.ExchangeId == ExchangeId && c.MarketId == MarketId && c.SymbolId == SymbolId && c.TimeFrameId == TimeFrameId, cancellationToken);

        public async Task<IEnumerable<CandleWorkerConfiguration>> GetConfigurations(CancellationToken cancellationToken)
            => await context.CandleWorkerConfigurations.AsNoTracking().ToListAsync(cancellationToken);
    }
}
