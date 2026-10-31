using Application.Dtos.Symbols;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.Symbols;

public sealed class AddSymbolCommandHandler(ISymbolCatalogRepository repository, AuditTrail audit) : ICommandHandler<AddSymbolDto, int>
{
    public async Task<int> HandleAsync(AddSymbolDto request, CancellationToken cancellationToken)
    {
        var name = SymbolNames.Normalize(request.Name);

        var description = SymbolNames.NormalizeDescription(request.Description);

        var id = await repository.AddSymbol(new SymbolLookup { Name = name, Description = description }, cancellationToken);
        await audit.RecordAsync(AuditAction.Added, AuditEntityTypes.Symbol, id, name, description, cancellationToken);
        return id;
    }
}
