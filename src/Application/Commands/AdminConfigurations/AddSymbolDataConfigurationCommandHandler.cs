using Application.Dtos.SymbolDataConfigurations;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public sealed class AddSymbolDataConfigurationCommandHandler(ISymbolDataConfigurationRepository repository, AuditTrail audit)
    : ICommandHandler<SymbolDataConfigurationDto, int>
{    
    public async Task<int> HandleAsync(SymbolDataConfigurationDto request, CancellationToken cancellationToken)
    {
        var configuration = SymbolDataValidation.ToValidConfiguration(request);
        var id = await repository.Add(configuration, cancellationToken);
        await audit.RecordAsync(AuditAction.Added, AuditEntityTypes.SymbolData, id,
            AuditDescriptions.Summary(configuration), AuditDescriptions.Values(configuration), cancellationToken);
        return id;
    }
}
