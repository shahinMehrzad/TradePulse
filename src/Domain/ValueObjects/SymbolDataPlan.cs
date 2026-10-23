using Domain.Entities;
using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// The funding rate and liquidations the admin wants collected for one symbol of one exchange market. The choices are taken
/// as stored: whether the market can really provide them is checked by whoever collects them.
/// </summary>
/// <param name="FundingStartDate">UTC. Where the funding rate history starts; null when the funding rate is not wanted.</param>
public readonly record struct SymbolDataPlan(
    ExchangeEnum Exchange, MarketTypeEnum MarketType, SymbolEnum Symbol,
    bool CollectFundingRate, DateTime? FundingStartDate, bool CollectLiquidation)
{
    /// <summary>
    /// Turns a stored configuration row (ids are the enum values) into a plan. False, with the reason, when an id is not a
    /// member of its enum. Whether an exchange is actually supported is the collector's business, not checked here.
    /// </summary>
    public static bool TryCreate(SymbolDataConfiguration row, out SymbolDataPlan plan, out string reason)
    {
        plan = default;

        if (!Enum.IsDefined((ExchangeEnum)row.ExchangeId))
            reason = "unknown exchange";
        else if (!Enum.IsDefined((MarketTypeEnum)row.MarketId))
            reason = "unknown market";
        else if (!Enum.IsDefined((SymbolEnum)row.SymbolId))
            reason = "unknown symbol";
        else
        {
            plan = new SymbolDataPlan(
                (ExchangeEnum)row.ExchangeId, (MarketTypeEnum)row.MarketId, (SymbolEnum)row.SymbolId,
                row.CollectFundingRate,
                // Stored as timestamp without a zone, always UTC.
                row.FundingStartDate is { } start ? DateTime.SpecifyKind(start, DateTimeKind.Utc) : null,
                row.CollectLiquidation);
            reason = string.Empty;
            return true;
        }

        return false;
    }
}
