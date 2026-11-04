using Application.Interfaces;
using Application.Research.Watch;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.Symbols;

public readonly record struct RemoveSymbolListingCommand(int Id);

public sealed class RemoveSymbolListingCommandHandler(ISymbolCatalogRepository repository, ITradedSymbolSource traded, AuditTrail audit) : ICommandHandler<RemoveSymbolListingCommand, bool>
{
    public async Task<bool> HandleAsync(RemoveSymbolListingCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        var existing = await repository.GetListing(request.Id, cancellationToken);

        // A symbol enabled for trading is watched on its own, with no row that points at its listing, so the database cannot refuse this.
        // Without the listing the watch would silently stop following a symbol that is still being traded.
        if (existing is not null && ConfiguredWatchedSymbols.IsTraded(existing, await traded.GetEnabledAsync(cancellationToken)))
            throw new ConfigurationConflictException("This symbol is enabled for trading, and its price watch follows it through this listing. Disable it on the Trading page first.");

        await repository.RemoveListing(request.Id, cancellationToken);

        if (existing is not null)
            await audit.RecordAsync(AuditAction.Removed, AuditEntityTypes.SymbolListing, existing.Id,
                AddSymbolListingCommandHandler.Summary((ExchangeEnum)existing.ExchangeId, (MarketTypeEnum)existing.MarketId, existing.Symbol!.Name, existing.ExchangeSymbol),
                null, cancellationToken);

        return true;
    }
}
