using Application.Interfaces;
using Application.Research.Watch;
using Application.Services;
using Domain.Entities;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public readonly record struct SetShockWatchEnabledCommand(int Id, bool Enabled);

/// <summary>
/// Switches the watch of one symbol on or off. This is also how the watch of a symbol that is enabled for trading is turned off: on the
/// admin's own responsibility, and remembered, so enabling the symbol for trading again does not switch it back on.
/// </summary>
public sealed class SetShockWatchEnabledCommandHandler(
    IShockWatchConfigurationRepository repository, ICandleWorkerConfigurationRepository series, AuditTrail audit)
    : ICommandHandler<SetShockWatchEnabledCommand, bool>
{
    public async Task<bool> HandleAsync(SetShockWatchEnabledCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        var existing = await repository.GetById(request.Id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Configuration does not exist.");

        if (existing.IsEnabled == request.Enabled)
            return true;

        // Only switching on needs the candles: switching off must always be possible.
        if (request.Enabled)
            await ShockWatchRules.EnsureMinuteCandlesAsync(series, existing.SymbolListingId, existing.SymbolListing!.ExchangeSymbol, cancellationToken);

        // Both versions are copies made before the change: a repository may hand out the very object it then updates.
        var before = Copy(existing, existing.IsEnabled);
        var after = Copy(existing, request.Enabled);

        await repository.SetEnabled(existing.Id, request.Enabled, cancellationToken);

        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.ShockWatch, existing.Id,
            AuditDescriptions.Summary(after), AuditDescriptions.Changes(before, after), cancellationToken);
        return true;
    }

    private static ShockWatchConfiguration Copy(ShockWatchConfiguration source, bool enabled) => new()
    {
        Id = source.Id, SymbolListingId = source.SymbolListingId, SymbolListing = source.SymbolListing, Reason = source.Reason, IsEnabled = enabled,
    };
}
