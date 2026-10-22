using Infrastructure.Configurations;

namespace BackgroundWorker.CandleSeries;

/// <summary>Where the candle series to collect come from (the admin's choice, stored in the config schema).</summary>
public interface ICandleSeriesSource
{
    /// <summary>The series to collect right now. Empty when the admin has chosen none. Throws when they cannot be read.</summary>
    Task<IReadOnlyList<BinanceSymbolConfigurations>> LoadAsync(CancellationToken cancellationToken);
}
