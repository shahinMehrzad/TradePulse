using Application.Interfaces;
using Application.Research.Watch;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public readonly record struct AddShockWatchCommand(int SymbolListingId);

/// <summary>Puts a symbol on the watch list by the admin's own choice. Returns the id of the row.</summary>
public sealed class AddShockWatchCommandHandler(
    IShockWatchConfigurationRepository repository, ISymbolCatalogRepository catalog, ICandleWorkerConfigurationRepository series, AuditTrail audit)
    : ICommandHandler<AddShockWatchCommand, int>
{
    public async Task<int> HandleAsync(AddShockWatchCommand request, CancellationToken cancellationToken)
    {
        if (request.SymbolListingId == default)
            throw new ConfigurationValidationException("Symbol listing is required.");

        var listing = await catalog.GetListing(request.SymbolListingId, cancellationToken)
            ?? throw new ConfigurationValidationException("Unknown symbol listing.");
        await ShockWatchRules.EnsureMinuteCandlesAsync(series, listing.Id, listing.ExchangeSymbol, cancellationToken);

        var configuration = new ShockWatchConfiguration { SymbolListingId = listing.Id, SymbolListing = listing, Reason = WatchReasonEnum.Manual, IsEnabled = true };
        var id = await repository.Add(configuration, cancellationToken);

        await audit.RecordAsync(AuditAction.Added, AuditEntityTypes.ShockWatch, id,
            AuditDescriptions.Summary(configuration), AuditDescriptions.Values(configuration), cancellationToken);
        return id;
    }
}
