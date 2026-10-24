using Domain.Entities;

namespace Domain.Repositories
{
    public interface ICandleWorkerConfigurationRepository
    {
        Task<IReadOnlyList<CandleWorkerConfiguration>> GetConfigurationList(CancellationToken cancellationToken);
        Task<CandleWorkerConfiguration?> GetConfiguration(int exchangeId, int marketId, int symbolId, int timeFrameId, CancellationToken cancellationToken);
        Task<int> AddNewCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken);
        Task<CandleWorkerConfiguration> UpdateCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken);
        Task RemoveCandleWorkerConfiguration(int configurationId, CancellationToken cancellationToken);
    }
}
