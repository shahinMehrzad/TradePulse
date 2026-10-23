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

        public async Task<CandleWorkerConfiguration?> GetConfiguration(int exchangeId, int marketId, int symbolId, int timeFrameId, CancellationToken cancellationToken)
            => await context.CandleWorkerConfigurations.AsNoTracking().SingleOrDefaultAsync(c => c.ExchangeId == exchangeId && c.MarketId == marketId && c.SymbolId == symbolId && c.TimeFrameId == timeFrameId, cancellationToken);


        public async Task<CandleWorkerConfiguration> UpdateCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken)
        {
            var config = await context.CandleWorkerConfigurations.SingleOrDefaultAsync(c => c.Id == configuration.Id, cancellationToken)
                ?? throw new ConfigurationNotFoundException("Configuration does not exist.");

            config.ExchangeId = configuration.ExchangeId;
            config.MarketId = configuration.MarketId;
            config.SymbolId = configuration.SymbolId;
            config.TimeFrameId = configuration.TimeFrameId;
            config.StartDate = DateTime.SpecifyKind(configuration.StartDate, DateTimeKind.Unspecified);
            config.CollectOpenInterest = configuration.CollectOpenInterest;
            await SaveAsync(cancellationToken);

            return config;
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
                throw new ConfigurationConflictException("Configuration already exists.", ex);
            }
            catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
            {
                throw new ConfigurationValidationException("Unknown exchange, market, symbol or time frame.");
            }
        }

        public async Task<IReadOnlyList<CandleWorkerConfiguration>> GetConfigurationList(CancellationToken cancellationToken)
            => await context.CandleWorkerConfigurations.OrderBy(c => c.Symbol.Name)
                .ThenBy(c => c.Id).AsNoTracking().ToListAsync(cancellationToken);

        public async Task<(IReadOnlyList<CandleWorkerConfiguration> result, int totalCount)> GetConfigurationsListByPagination(int pageNumber, int pageSize, string? keyword, CancellationToken cancellationToken)
        {
            var query = context.CandleWorkerConfigurations.AsQueryable();
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                var term = keyword.Trim().ToLower();
                query = query.Where(c => (c.Exchange != null && c.Exchange.Name.ToLower().Contains(term)) || (c.Market != null && c.Market.Name.ToLower().Contains(term)) || (c.Symbol != null && c.Symbol.Name.ToLower().Contains(term)) || (c.Symbol != null && c.Symbol.Description != null && c.Symbol.Description.ToLower().Contains(term)));
            }
            var totalCount = await query.CountAsync(cancellationToken);
            return (await query
                .OrderBy(c => c.Symbol.Name)
                .ThenBy(c => c.TimeFrame.IntervalToSeconds)
                .ThenBy(c => c.Id)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .AsNoTracking().ToListAsync(cancellationToken), totalCount);
        }
    }
}
