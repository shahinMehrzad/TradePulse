using Application.Dtos.SymbolDataConfigurations;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public sealed class AddSymbolDataConfigurationCommandHandler(ISymbolDataConfigurationRepository repository, ISymbolCatalogRepository catalog, AuditTrail audit)
    : ICommandHandler<SymbolDataConfigurationDto, int>
{    
    public async Task<int> HandleAsync(SymbolDataConfigurationDto request, CancellationToken cancellationToken)
    {
        if (request.SymbolListingId == default)
            throw new ConfigurationValidationException("Symbol listing is required.");

        var listing = await catalog.GetListing(request.SymbolListingId, cancellationToken)
            ?? throw new ConfigurationValidationException("Unknown symbol listing.");
        var configuration = SymbolDataValidation.ToValidConfiguration(request, listing);
        var id = await repository.Add(configuration, cancellationToken);
        await audit.RecordAsync(AuditAction.Added, AuditEntityTypes.SymbolData, id,
            AuditDescriptions.Summary(configuration), AuditDescriptions.Values(configuration), cancellationToken);
        return id;
    }
}
