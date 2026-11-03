using Application.Interfaces;
using Application.Research.Watch;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public readonly record struct SwitchOffTradedWatchCommand(int SymbolListingId);

/// <summary>
/// Turns off the watch of a symbol that is enabled for trading and has no row on the list (it is watched on its own). The admin does
/// this on their own responsibility, and it is remembered as a row (<see cref="WatchReasonEnum.Trading"/>, off), so the symbol stays
/// unwatched until the admin switches it on again. Returns the id of that row.
/// </summary>
public sealed class SwitchOffTradedWatchCommandHandler(
    IShockWatchConfigurationRepository repository, ISymbolCatalogRepository catalog, ITradedSymbolSource traded, AuditTrail audit)
    : ICommandHandler<SwitchOffTradedWatchCommand, int>
{
    public async Task<int> HandleAsync(SwitchOffTradedWatchCommand request, CancellationToken cancellationToken)
    {
        if (request.SymbolListingId == default)
            throw new ConfigurationValidationException("Symbol listing is required.");

        var listing = await catalog.GetListing(request.SymbolListingId, cancellationToken)
            ?? throw new ConfigurationValidationException("Unknown symbol listing.");

        if (!ConfiguredWatchedSymbols.TradedListings([listing], await traded.GetEnabledAsync(cancellationToken)).Any())
            throw new ConfigurationValidationException($"{listing.ExchangeSymbol} is not enabled for trading, so its watch is not automatic. Add it by hand to watch it.");

        var existing = (await repository.GetAll(cancellationToken)).FirstOrDefault(c => c.SymbolListingId == listing.Id);
        if (existing is not null)
        {
            // A row already decides for this symbol (one added by hand, or an earlier switch-off): switching off is just switching it.
            if (existing.IsEnabled)
            {
                await repository.SetEnabled(existing.Id, false, cancellationToken);
                await RecordAsync(existing.Id, listing, existing.Reason, cancellationToken);
            }

            return existing.Id;
        }

        var configuration = new ShockWatchConfiguration { SymbolListingId = listing.Id, SymbolListing = listing, Reason = WatchReasonEnum.Trading, IsEnabled = false };
        var id = await repository.Add(configuration, cancellationToken);
        await RecordAsync(id, listing, WatchReasonEnum.Trading, cancellationToken);
        return id;
    }

    private Task RecordAsync(int id, SymbolListing listing, WatchReasonEnum reason, CancellationToken cancellationToken)
    {
        var row = new ShockWatchConfiguration { Id = id, SymbolListingId = listing.Id, SymbolListing = listing, Reason = reason, IsEnabled = false };
        return audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.ShockWatch, id,
            AuditDescriptions.Summary(row), "watch on -> off (enabled for trading)", cancellationToken);
    }
}
