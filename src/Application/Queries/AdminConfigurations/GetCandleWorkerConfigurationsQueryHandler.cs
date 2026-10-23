using Application.Interfaces;
using Domain.Entities;
using Domain.Repositories;

namespace Application.Queries.AdminConfigurations;

public readonly record struct GetCandleWorkerConfigurationsQuery();
public sealed class GetCandleWorkerConfigurationsQueryHandler(ICandleWorkerConfigurationRepository workerConfigurationRepository)
    : ICommandHandler<GetCandleWorkerConfigurationsQuery, IReadOnlyList<CandleWorkerConfiguration>>
{
    private readonly ICandleWorkerConfigurationRepository _workerConfigurationRepository = workerConfigurationRepository;
    public async Task<IReadOnlyList<CandleWorkerConfiguration>> HandleAsync(GetCandleWorkerConfigurationsQuery request, CancellationToken cancellationToken)
        => await _workerConfigurationRepository.GetConfigurationList(cancellationToken);
}
