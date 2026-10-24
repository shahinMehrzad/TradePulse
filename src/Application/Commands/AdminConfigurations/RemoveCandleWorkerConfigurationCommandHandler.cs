using Application.Interfaces;
using Domain.Exceptions;
using Domain.Entities;
using Domain.Repositories;
using Application.Services;

namespace Application.Commands.AdminConfigurations;

public readonly record struct RemoveCandleWorkerConfigurationCommand(int Id);
public sealed class RemoveCandleWorkerConfigurationCommandHandler(ICandleWorkerConfigurationRepository workerConfigurationRepository, AuditTrail audit)
    : ICommandHandler<RemoveCandleWorkerConfigurationCommand, bool>
{
    public async Task<bool> HandleAsync(RemoveCandleWorkerConfigurationCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        var existing = await workerConfigurationRepository.GetConfigurationById(request.Id, cancellationToken);

        // Throws ConfigurationNotFoundException when there is no such row, so reaching the next line means it was deleted.
        await workerConfigurationRepository.RemoveCandleWorkerConfiguration(request.Id, cancellationToken);

        if (existing is not null)
            await audit.RecordAsync(AuditAction.Removed, AuditEntityTypes.CandleSeries, existing.Id,
                AuditDescriptions.Summary(existing), AuditDescriptions.Values(existing), cancellationToken);

        return true;
    }
}
