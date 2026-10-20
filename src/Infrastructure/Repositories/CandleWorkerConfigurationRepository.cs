using Domain.Entities;
using Domain.Repositories;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Repositories
{
    public class CandleWorkerConfigurationRepository(ConfigDbContext context) : ICandleWorkerConfigurationRepository
    {
        public async Task<int> AddNewCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken)
        {
            try
            {
                var res = await context.CandleWorkerConfigurations.AddAsync(configuration, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
                return configuration.Id;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public async Task<CandleWorkerConfiguration?> GetConfiguration(int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, CancellationToken cancellationToken)
            => await context.CandleWorkerConfigurations.AsNoTracking().SingleOrDefaultAsync(c => c.ExchangeId == ExchangeId && c.MarketId == MarketId && c.SymbolId == SymbolId && c.TimeFrameId == TimeFrameId, cancellationToken);

        public async Task<IEnumerable<CandleWorkerConfiguration>> GetConfigurations(CancellationToken cancellationToken)
            => await context.CandleWorkerConfigurations.AsNoTracking().ToListAsync(cancellationToken);
    }
}
