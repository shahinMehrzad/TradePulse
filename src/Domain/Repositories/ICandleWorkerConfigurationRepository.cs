using Domain.Entities;

namespace Domain.Repositories
{
    public interface ICandleWorkerConfigurationRepository
    {
        Task<IReadOnlyList<CandleWorkerConfiguration>> GetConfigurationList(CancellationToken cancellationToken);
        Task<CandleWorkerConfiguration?> GetConfigurationById(int id, CancellationToken cancellationToken);
        Task<CandleWorkerConfiguration?> GetConfiguration(int symbolListingId, int timeFrameId, CancellationToken cancellationToken);
        Task<int> AddNewCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken);
        Task<CandleWorkerConfiguration> UpdateCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken);
        Task RemoveCandleWorkerConfiguration(int configurationId, CancellationToken cancellationToken);
    }
}
