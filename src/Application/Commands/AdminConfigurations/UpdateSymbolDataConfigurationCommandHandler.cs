using Application.Dtos.SymbolDataConfigurations;
using Application.Interfaces;
using Domain.Entities;
using Domain.Exceptions;
using Application.Services;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public sealed class UpdateSymbolDataConfigurationCommandHandler(ISymbolDataConfigurationRepository repository, ISymbolCatalogRepository catalog, AuditTrail audit)
    : ICommandHandler<UpdateSymbolDataConfigurationDto, SymbolDataConfiguration>
{
    public async Task<SymbolDataConfiguration> HandleAsync(UpdateSymbolDataConfigurationDto request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        if (request.SymbolListingId == default)
            throw new ConfigurationValidationException("Symbol listing is required.");

        var listing = await catalog.GetListing(request.SymbolListingId, cancellationToken)
            ?? throw new ConfigurationValidationException("Unknown symbol listing.");
        var configuration = SymbolDataValidation.ToValidConfiguration(request, listing);
        configuration.Id = request.Id;

        // A row that does not exist is reported by the update itself.
        var before = await repository.GetById(request.Id, cancellationToken);

        var updated = await repository.Update(configuration, cancellationToken);

        if (before is not null && AuditDescriptions.Changes(before, updated) is { } changes)
            await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.SymbolData, updated.Id, AuditDescriptions.Summary(updated), changes, cancellationToken);

        return updated;
    }
}
