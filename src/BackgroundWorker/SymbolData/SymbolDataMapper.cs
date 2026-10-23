using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace BackgroundWorker.SymbolData;

/// <summary>
/// Turns the rows the admin stored into the plans the workers run on. A row whose id is not a member of an enum is skipped
/// and reported instead of crashing the collector. Rows for any exchange are kept: each exchange's workers pick their own
/// with <see cref="ForExchange"/>.
/// </summary>
public static class SymbolDataMapper
{
    public static IReadOnlyList<SymbolDataPlan> ToPlans(
        IEnumerable<SymbolDataConfiguration> rows, Action<SymbolDataConfiguration, string>? onSkipped = null)
    {
        var plans = new List<SymbolDataPlan>();

        foreach (var row in rows)
        {
            if (SymbolDataPlan.TryCreate(row, out var plan, out var reason))
                plans.Add(plan);
            else
                onSkipped?.Invoke(row, reason);
        }

        // The unique index already forbids a symbol twice; the workers would double-collect it, so be sure.
        return plans.DistinctBy(p => (p.Exchange, p.MarketType, p.Symbol)).ToList();
    }

    /// <summary>The plans one exchange's workers are responsible for.</summary>
    public static IEnumerable<SymbolDataPlan> ForExchange(this IEnumerable<SymbolDataPlan> plans, ExchangeEnum exchange) =>
        plans.Where(p => p.Exchange == exchange);
}
