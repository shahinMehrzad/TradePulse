using Application.Research.Watch;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Commands.AdminConfigurations;

/// <summary>What has to be true before a symbol may be watched.</summary>
internal static class ShockWatchRules
{
    /// <summary>
    /// The watch starts from the last hour of the symbol's 1 minute candles, so those must be collected. Without them it would sit
    /// silent for an hour after every start, which the admin must not find out only when a move goes by unnoticed.
    /// </summary>
    public static async Task EnsureMinuteCandlesAsync(
        ICandleWorkerConfigurationRepository series, int symbolListingId, string exchangeSymbol, CancellationToken cancellationToken)
    {
        if (await series.GetConfiguration(symbolListingId, (int)TimeFrameEnum._1M, cancellationToken) is null)
            throw new ConfigurationValidationException($"Collect the 1 minute candles of {exchangeSymbol} first: the watch starts from them.");
    }

    /// <summary>
    /// Keeps a symbol that is enabled for trading unwatched, on the admin's own responsibility. What does that is a row of kind
    /// <see cref="WatchReasonEnum.Trading"/> that is off, so a row added by hand becomes one (its "off" alone would not do: enabling a symbol
    /// for trading watches it again). A row that already is one is left as it is.
    /// </summary>
    public static async Task SwitchOffTradedAsync(
        IShockWatchConfigurationRepository repository, AuditTrail audit, ShockWatchConfiguration existing, CancellationToken cancellationToken)
    {
        if (existing.Reason == WatchReasonEnum.Trading && !existing.IsEnabled)
            return;

        var listing = existing.SymbolListing;
        var id = existing.Id;
        var symbolListingId = existing.SymbolListingId;
        await repository.Decide(id, WatchReasonEnum.Trading, false, cancellationToken);

        var after = new ShockWatchConfiguration { Id = id, SymbolListingId = symbolListingId, SymbolListing = listing, Reason = WatchReasonEnum.Trading, IsEnabled = false };
        await audit.RecordAsync(AuditAction.Updated, AuditEntityTypes.ShockWatch, id,
            AuditDescriptions.Summary(after), "watch on -> off (enabled for trading)", cancellationToken);
    }
}
