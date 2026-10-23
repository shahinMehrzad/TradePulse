using BackgroundWorker.Plans;
using Domain.ValueObjects;

namespace BackgroundWorker.SymbolData;

/// <summary>Where the funding rate and liquidation choices come from (the admin's, stored in the config schema).</summary>
public interface ISymbolDataSource : IPlanSource<SymbolDataPlan>;
