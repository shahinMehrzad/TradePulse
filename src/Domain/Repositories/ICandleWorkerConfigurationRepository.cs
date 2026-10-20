using Domain.Entities;

namespace Domain.Repositories
{
    public interface ICandleWorkerConfigurationRepository
    {
        Task<IEnumerable<CandleWorkerConfiguration>> GetConfigurations(CancellationToken cancellationToken);
        Task<CandleWorkerConfiguration?> GetConfiguration(int ExchangeId, int MarketId, int SymbolId, int TimeFrameId, CancellationToken cancellationToken);
        /// <summary>Saves the series and returns its new id. <c>StartDate</c> must already be UTC. Throws a conflict exception for a duplicate and a validation exception for an unknown id.</summary>
        Task<int> AddNewCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken);
    }
}
