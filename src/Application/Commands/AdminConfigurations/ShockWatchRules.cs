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
}
