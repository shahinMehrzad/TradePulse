using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.Symbols;

public readonly record struct RemoveSymbolCommand(int Id);

public sealed class RemoveSymbolCommandHandler(ISymbolCatalogRepository repository, AuditTrail audit) : ICommandHandler<RemoveSymbolCommand, bool>
{
    public async Task<bool> HandleAsync(RemoveSymbolCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        var existing = await repository.GetSymbol(request.Id, cancellationToken);

        await repository.RemoveSymbol(request.Id, cancellationToken);

        if (existing is not null)
            await audit.RecordAsync(AuditAction.Removed, AuditEntityTypes.Symbol, existing.Id, existing.Name, existing.Description, cancellationToken);

        return true;
    }
}
