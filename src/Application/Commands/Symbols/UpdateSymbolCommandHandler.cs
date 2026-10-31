using Application.Dtos.Symbols;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.Symbols;

public sealed class UpdateSymbolCommandHandler(ISymbolCatalogRepository repository, AuditTrail audit) : ICommandHandler<UpdateSymbolDto, SymbolLookup>
{
    public async Task<SymbolLookup> HandleAsync(UpdateSymbolDto request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        var description = SymbolNames.NormalizeDescription(request.Description);

        // A symbol that does not exist is reported by the update itself.
        var before = await repository.GetSymbol(request.Id, cancellationToken);

        var updated = await repository.UpdateSymbolDescription(request.Id, description, cancellationToken);

        if (before is not null && before.Description != updated.Description)
            await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.Symbol, updated.Id, updated.Name,
                $"description {Show(before.Description)} -> {Show(updated.Description)}", cancellationToken);

        return updated;
    }

    private static string Show(string? description) => description is null ? "none" : $"'{description}'";
}
