using BackgroundWorker.Plans;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace BackgroundWorker.SymbolData;

/// <summary>
/// The funding rate and liquidation choices the admin made: the one list the funding rate and liquidation workers wait for
/// (<see cref="PlanProvider{TPlan}.WaitForPlansAsync"/>). See <see cref="PlanProvider{TPlan}"/> for how it is read and when it changes.
/// </summary>
public sealed class SymbolDataProvider(ISymbolDataSource source, TimeProvider timeProvider, ILogger<SymbolDataProvider> logger)
    : PlanProvider<SymbolDataPlan>(source, timeProvider, logger, "symbol data configurations");
