using Application.Dtos.SymbolDataConfigurations;
using Application.Interfaces;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public sealed class AddSymbolDataConfigurationCommandHandler(ISymbolDataConfigurationRepository repository)
    : ICommandHandler<SymbolDataConfigurationDto, int>
{    
    public async Task<int> HandleAsync(SymbolDataConfigurationDto request, CancellationToken cancellationToken)
        => await repository.Add(SymbolDataValidation.ToValidConfiguration(request), cancellationToken);
}
