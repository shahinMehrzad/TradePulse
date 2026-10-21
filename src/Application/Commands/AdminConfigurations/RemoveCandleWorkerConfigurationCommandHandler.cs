using Application.Interfaces;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public readonly record struct RemoveCandleWorkerConfigurationCommand(int Id);
public sealed class RemoveCandleWorkerConfigurationCommandHandler(ICandleWorkerConfigurationRepository workerConfigurationRepository)
    : ICommandHandler<RemoveCandleWorkerConfigurationCommand, bool>
{
    public async Task<bool> HandleAsync(RemoveCandleWorkerConfigurationCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        // Throws ConfigurationNotFoundException when there is no such row, so reaching the next line means it was deleted.
        await workerConfigurationRepository.RemoveCandleWorkerConfiguration(request.Id, cancellationToken);
        return true;
    }
}
