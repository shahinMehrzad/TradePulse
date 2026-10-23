using Application.Dtos.SymbolDataConfigurations;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public sealed class UpdateSymbolDataConfigurationCommandHandler(ISymbolDataConfigurationRepository repository)
    : ICommandHandler<UpdateSymbolDataConfigurationDto, SymbolDataConfiguration>
{
    public async Task<SymbolDataConfiguration> HandleAsync(UpdateSymbolDataConfigurationDto request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        var configuration = SymbolDataValidation.ToValidConfiguration(request);
        configuration.Id = request.Id;

        return await repository.Update(configuration, cancellationToken);
    }
}
