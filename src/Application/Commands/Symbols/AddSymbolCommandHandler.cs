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

        var description = request.Description?.Trim();
        if (description is { Length: > SymbolNames.MaxDescriptionLength })
            throw new ConfigurationValidationException($"Description can have at most {SymbolNames.MaxDescriptionLength} characters.");

        var id = await repository.AddSymbol(new SymbolLookup { Name = name, Description = string.IsNullOrEmpty(description) ? null : description }, cancellationToken);
        await audit.RecordAsync(AuditAction.Added, AuditEntityTypes.Symbol, id, name, description, cancellationToken);
        return id;
    }
}
