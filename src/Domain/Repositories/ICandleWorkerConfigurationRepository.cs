using Domain.Entities;

namespace Domain.Repositories
{
    public interface ICandleWorkerConfigurationRepository
    {
        Task<IEnumerable<CandleWorkerConfiguration>> GetConfigurationList(CancellationToken cancellationToken);
        Task<(IEnumerable<CandleWorkerConfiguration> result, int totalCount)> GetConfigurationsListByPagination(int pageNumber, int pageSize, string? keyword, CancellationToken cancellationToken);
        Task<CandleWorkerConfiguration?> GetConfiguration(int exchangeId, int marketId, int symbolId, int timeFrameId, CancellationToken cancellationToken);
        Task<int> AddNewCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken);
        Task<CandleWorkerConfiguration> UpdateCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken);
        Task RemoveCandleWorkerConfiguration(int configurationId, CancellationToken cancellationToken);
    }
}
