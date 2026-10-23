using Domain.Enums;
using Domain.Services;
using Domain.ValueObjects;

namespace BackgroundWorker.SymbolData;

/// <summary>A symbol whose funding rate is collected, and the date its history starts from (UTC).</summary>
public readonly record struct FundingRateTarget(SymbolEnum Symbol, DateTime StartDate);

/// <summary>
/// Which of the admin's choices the Binance funding rate and liquidation workers can really serve. A choice the market cannot
/// honour (a row edited by hand in the database; the admin API refuses it) is reported through <c>onRefused</c> and left out,
/// so one bad row never stops the others.
/// </summary>
public static class SymbolDataPlans
{
    public static IReadOnlyList<FundingRateTarget> FundingRatesForBinance(IEnumerable<SymbolDataPlan> plans, Action<SymbolDataPlan, string>? onRefused = null)
    {
        var targets = new List<FundingRateTarget>();

        foreach (var plan in plans.ForExchange(ExchangeEnum.Binance).Where(p => p.CollectFundingRate))
        {
            if (!MarketDataSupport.TryValidate(plan.Exchange, plan.MarketType, MarketDataTypeEnum.FundingRate, timeFrame: null, out var reason))
                onRefused?.Invoke(plan, reason);
            else if (plan.FundingStartDate is not { } start)
                onRefused?.Invoke(plan, "the funding rate has no start date");
            else
                targets.Add(new FundingRateTarget(plan.Symbol, start));
        }

        return targets.DistinctBy(t => t.Symbol).ToList();
    }

    public static IReadOnlyList<SymbolEnum> LiquidationsForBinance(IEnumerable<SymbolDataPlan> plans, Action<SymbolDataPlan, string>? onRefused = null)
    {
        var symbols = new List<SymbolEnum>();

        foreach (var plan in plans.ForExchange(ExchangeEnum.Binance).Where(p => p.CollectLiquidation))
        {
            if (!MarketDataSupport.TryValidate(plan.Exchange, plan.MarketType, MarketDataTypeEnum.Liquidation, timeFrame: null, out var reason))
                onRefused?.Invoke(plan, reason);
            else
                symbols.Add(plan.Symbol);
        }

        return symbols.Distinct().ToList();
    }
}
