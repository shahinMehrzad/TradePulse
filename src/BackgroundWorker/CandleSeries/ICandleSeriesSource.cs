using BackgroundWorker.Plans;
using Domain.ValueObjects;

namespace BackgroundWorker.CandleSeries;

/// <summary>Where the candle series to collect come from (the admin's choice, stored in the config schema).</summary>
public interface ICandleSeriesSource : IPlanSource<CandleSeriesPlan>;
