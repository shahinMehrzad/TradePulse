using Domain.Entities;

namespace Domain.Repositories
{
    public interface ICandleWorkerConfigurationRepository
    {
        Task<IEnumerable<CandleWorkerConfiguration>> GetConfigurations(CancellationToken cancellationToken);
        Task<CandleWorkerConfiguration?> GetConfiguration(int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, CancellationToken cancellationToken);
        Task<int> AddNewCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken);
    }
}
