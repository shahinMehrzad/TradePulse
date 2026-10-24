using Application.Interfaces;
using Domain.Exceptions;
using Application.Services;
using Domain.Entities;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public readonly record struct RemoveSymbolDataConfigurationCommand(int Id);

public sealed class RemoveSymbolDataConfigurationCommandHandler(ISymbolDataConfigurationRepository repository, AuditTrail audit)
    : ICommandHandler<RemoveSymbolDataConfigurationCommand, bool>
{
    public async Task<bool> HandleAsync(RemoveSymbolDataConfigurationCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        var existing = await repository.GetById(request.Id, cancellationToken);

        await repository.Remove(request.Id, cancellationToken);

        if (existing is not null)
            await audit.RecordAsync(AuditAction.Removed, AuditEntityTypes.SymbolData, existing.Id,
                AuditDescriptions.Summary(existing), AuditDescriptions.Values(existing), cancellationToken);

        return true;
    }
}
