using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.Symbols;

public readonly record struct RemoveSymbolListingCommand(int Id);

public sealed class RemoveSymbolListingCommandHandler(ISymbolCatalogRepository repository, AuditTrail audit) : ICommandHandler<RemoveSymbolListingCommand, bool>
{
    public async Task<bool> HandleAsync(RemoveSymbolListingCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        var existing = await repository.GetListing(request.Id, cancellationToken);

        await repository.RemoveListing(request.Id, cancellationToken);

        if (existing is not null)
            await audit.RecordAsync(AuditAction.Removed, AuditEntityTypes.SymbolListing, existing.Id,
                AddSymbolListingCommandHandler.Summary((ExchangeEnum)existing.ExchangeId, (MarketTypeEnum)existing.MarketId, existing.Symbol!.Name, existing.ExchangeSymbol),
                null, cancellationToken);

        return true;
    }
}
