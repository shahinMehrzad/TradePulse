using Application.Interfaces;
using Domain.Entities;
using Domain.Repositories;

namespace Application.Queries.AdminConfigurations;

public readonly record struct GetCandleWorkerConfigurationsQuery();
public sealed class GetCandleWorkerConfigurationsQueryHandler(ICandleWorkerConfigurationRepository workerConfigurationRepository)
    : ICommandHandler<GetCandleWorkerConfigurationsQuery, IEnumerable<CandleWorkerConfiguration>>
{
    private readonly ICandleWorkerConfigurationRepository _workerConfigurationRepository = workerConfigurationRepository;
    public async Task<IEnumerable<CandleWorkerConfiguration>> HandleAsync(GetCandleWorkerConfigurationsQuery request, CancellationToken cancellationToken)
        => await _workerConfigurationRepository.GetConfigurations(cancellationToken);      
}
