using BackgroundWorker.Plans;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.CandleSeries;

/// <summary>
/// The candle series the admin chose (and which of them also collect open interest): the one list every candle and open
/// interest worker waits for. See <see cref="PlanProvider{TPlan}"/> for how it is read and when it changes.
/// </summary>
public sealed class CandleSeriesProvider(ICandleSeriesSource source, TimeProvider timeProvider, ILogger<CandleSeriesProvider> logger)
    : PlanProvider<CandleSeriesPlan>(source, timeProvider, logger, "candle series")
{
    /// <summary>Completes with the series as soon as there is at least one. Cancelling the token (shutdown) cancels the wait.</summary>
    public Task<IReadOnlyList<CandleSeriesPlan>> WaitForSeriesAsync(CancellationToken cancellationToken) => WaitForPlansAsync(cancellationToken);
}
