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
/// Turns off the watch of a symbol that is enabled for trading (it is watched on its own, or through a row added by hand). The admin does
/// this on their own responsibility, and it is remembered as a row (<see cref="WatchReasonEnum.Trading"/>, off), so the symbol stays
/// unwatched until the admin switches it on again. A row added by hand becomes that row. Returns the id of the row.
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
            // A row says something about this symbol already (one added by hand, or an earlier switch-off): it becomes the "off" of a
            // traded symbol, or stays one.
            await ShockWatchRules.SwitchOffTradedAsync(repository, audit, existing, cancellationToken);
            return existing.Id;
        }

        var configuration = new ShockWatchConfiguration { SymbolListingId = listing.Id, SymbolListing = listing, Reason = WatchReasonEnum.Trading, IsEnabled = false };
        var id = await repository.Add(configuration, cancellationToken);
        configuration.Id = id;
        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.ShockWatch, id,
            AuditDescriptions.Summary(configuration), "watch on -> off (enabled for trading)", cancellationToken);
        return id;
    }
}
