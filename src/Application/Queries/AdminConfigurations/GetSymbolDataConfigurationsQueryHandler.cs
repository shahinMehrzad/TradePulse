using Application.Interfaces;
using Domain.Entities;
using Domain.Repositories;

namespace Application.Queries.AdminConfigurations;

public readonly record struct GetSymbolDataConfigurationsQuery();

public sealed class GetSymbolDataConfigurationsQueryHandler(ISymbolDataConfigurationRepository repository)
    : ICommandHandler<GetSymbolDataConfigurationsQuery, IReadOnlyList<SymbolDataConfiguration>>
{
    public async Task<IReadOnlyList<SymbolDataConfiguration>> HandleAsync(GetSymbolDataConfigurationsQuery request, CancellationToken cancellationToken)
        => await repository.GetAll(cancellationToken);
}
