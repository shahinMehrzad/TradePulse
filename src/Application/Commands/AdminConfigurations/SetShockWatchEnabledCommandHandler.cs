using Application.Interfaces;
using Application.Research.Watch;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

public readonly record struct SetShockWatchEnabledCommand(int Id, bool Enabled);

/// <summary>
/// Switches the watch of one row on or off.
/// <para>
/// A row the admin added by hand is just switched, except that switching off the row of a symbol that is enabled for trading makes it the
/// "off" of a traded symbol (<see cref="ShockWatchRules.SwitchOffTradedAsync"/>). A row of a symbol that is enabled for trading only exists because the admin turned
/// its watch off (on their own responsibility): switching it on again does not keep a row that says "on", it <b>removes</b> the row, so the
/// symbol goes back to being watched on its own for as long as it is traded, and stops being watched the moment it is not.
/// </para>
/// </summary>
public sealed class SetShockWatchEnabledCommandHandler(
    IShockWatchConfigurationRepository repository, ICandleWorkerConfigurationRepository series, ITradedSymbolSource traded, AuditTrail audit)
    : ICommandHandler<SetShockWatchEnabledCommand, bool>
{
    public async Task<bool> HandleAsync(SetShockWatchEnabledCommand request, CancellationToken cancellationToken)
    {
        if (request.Id == default)
            throw new ConfigurationValidationException("Id is required.");

        var existing = await repository.GetById(request.Id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Configuration does not exist.");

        // Switching off the row of a symbol that is enabled for trading cannot be just "off": the symbol would be watched all the same.
        if (!request.Enabled && existing.Reason == WatchReasonEnum.Manual
            && ConfiguredWatchedSymbols.IsTraded(existing.SymbolListing!, await traded.GetEnabledAsync(cancellationToken)))
        {
            await ShockWatchRules.SwitchOffTradedAsync(repository, audit, existing, cancellationToken);
            return true;
        }

        var backToAutomatic = existing.Reason == WatchReasonEnum.Trading && request.Enabled;
        if (existing.IsEnabled == request.Enabled && !backToAutomatic)
            return true;

        // Only a row of the admin's own needs the candles to be switched on: a traded symbol cannot be refused its watch, and the watch
        // tells the admin when it has no candles to start from. Switching off must always be possible.
        if (request.Enabled && !backToAutomatic)
            await ShockWatchRules.EnsureMinuteCandlesAsync(series, existing.SymbolListingId, existing.SymbolListing!.ExchangeSymbol, cancellationToken);

        // Both versions are copies made before the change: a repository may hand out the very object it then updates.
        var before = Copy(existing, existing.IsEnabled);
        var after = Copy(existing, request.Enabled);

        if (backToAutomatic)
            await repository.Remove(existing.Id, cancellationToken);
        else
            await repository.SetEnabled(existing.Id, request.Enabled, cancellationToken);

        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.ShockWatch, existing.Id,
            AuditDescriptions.Summary(after), AuditDescriptions.Changes(before, after) + (backToAutomatic ? " (automatic again: enabled for trading)" : ""), cancellationToken);
        return true;
    }

    private static ShockWatchConfiguration Copy(ShockWatchConfiguration source, bool enabled) => new()
    {
        Id = source.Id, SymbolListingId = source.SymbolListingId, SymbolListing = source.SymbolListing, Reason = source.Reason, IsEnabled = enabled,
    };
}
