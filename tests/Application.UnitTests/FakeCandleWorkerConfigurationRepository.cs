using Domain.Entities;
using Domain.Repositories;

namespace Application.UnitTests;

internal sealed class FakeCandleWorkerConfigurationRepository : ICandleWorkerConfigurationRepository
{
    public List<CandleWorkerConfiguration> Configurations { get; } = [];

    public Task<IReadOnlyList<CandleWorkerConfiguration>> GetConfigurationList(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CandleWorkerConfiguration>>(Configurations.ToList());

    public Task<(IReadOnlyList<CandleWorkerConfiguration> result, int totalCount)> GetConfigurationsListByPagination(int pageNumber, int pageSize, string? keyword, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<CandleWorkerConfiguration?> GetConfiguration(int exchangeId, int marketId, int symbolId, int timeFrameId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<int> AddNewCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<CandleWorkerConfiguration> UpdateCandleWorkerConfiguration(CandleWorkerConfiguration configuration, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task RemoveCandleWorkerConfiguration(int configurationId, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
