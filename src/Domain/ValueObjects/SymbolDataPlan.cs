using Domain.Entities;
using Domain.Enums;
using Domain.Services;

namespace Domain.ValueObjects;

/// <summary>
/// The funding rate and liquidations the admin wants collected for one symbol of one exchange market. The choices are taken
/// as stored: whether the market can really provide them is checked by whoever collects them.
/// </summary>
/// <param name="FundingStartDate">UTC. Where the funding rate history starts; null when the funding rate is not wanted.</param>
public readonly record struct SymbolDataPlan(
    ExchangeEnum Exchange, MarketTypeEnum MarketType, ExchangeSymbol Symbol,
    bool CollectFundingRate, DateTime? FundingStartDate, bool CollectLiquidation)
{
    /// <summary>
    /// Turns a stored configuration row (its <see cref="SymbolDataConfiguration.SymbolListing"/> loaded) into a plan. False, with
    /// the reason, when the symbol is not listed, an id is not a member of its enum or the symbol's name cannot be part of a
    /// table name. Whether an exchange is actually supported is the collector's business, not checked here.
    /// </summary>
    public static bool TryCreate(SymbolDataConfiguration row, out SymbolDataPlan plan, out string reason)
    {
        plan = default;

        if (row.SymbolListing is not { } listing)
            reason = "the symbol is not listed on this exchange market";
        else if (!Enum.IsDefined((ExchangeEnum)listing.ExchangeId))
            reason = "unknown exchange";
        else if (!Enum.IsDefined((MarketTypeEnum)listing.MarketId))
            reason = "unknown market";
        else if (!TableNameRegistry.TryGetSymbolPart((ExchangeEnum)listing.ExchangeId, (MarketTypeEnum)listing.MarketId, listing.ExchangeSymbol, out _, out var symbolReason))
            reason = $"unusable symbol name: {symbolReason}";
        else
        {
            plan = new SymbolDataPlan(
                (ExchangeEnum)listing.ExchangeId, (MarketTypeEnum)listing.MarketId, new ExchangeSymbol(listing.ExchangeSymbol),
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
