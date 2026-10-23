using Application.Interfaces;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public readonly record struct RemoveSymbolDataConfigurationCommand(int Id);

public sealed class RemoveSymbolDataConfigurationCommandHandler(ISymbolDataConfigurationRepository repository)
    : ICommandHandler<RemoveSymbolDataConfigurationCommand, bool>
{
    public async Task<bool> HandleAsync(RemoveSymbolDataConfigurationCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        await repository.Remove(request.Id, cancellationToken);
        return true;
    }
}
