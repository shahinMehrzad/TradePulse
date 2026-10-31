using Domain.Entities;
using Domain.Enums;
using Domain.Services;

namespace Domain.ValueObjects;

/// <summary>
/// A series the admin wants collected, the date its history should start from (UTC), and whether its open interest is wanted too.
/// <c>CollectMarketStatistics</c> asks for the long/short ratio and taker volume of the series. The flags are taken as stored:
/// whether the market can really provide the data is checked by whoever collects it.
/// </summary>
public readonly record struct CandleSeriesPlan(CandleSeries Series, DateTime StartDate, bool CollectOpenInterest = false, bool CollectMarketStatistics = false)
{
    /// <summary>
    /// Turns a stored configuration row (its <see cref="CandleWorkerConfiguration.SymbolListing"/> loaded) into a plan. False,
    /// with the reason, when the symbol is not listed, an id is not a member of its enum or the symbol's name cannot be part of
    /// a table name. Whether an exchange is actually supported is the collector's business, not checked here.
    /// </summary>
    public static bool TryCreate(CandleWorkerConfiguration row, out CandleSeriesPlan plan, out string reason)
    {
        plan = default;

        if (row.SymbolListing is not { } listing)
            reason = "the symbol is not listed on this exchange market";
        else if (!Enum.IsDefined((ExchangeEnum)listing.ExchangeId))
            reason = "unknown exchange";
        else if (!Enum.IsDefined((MarketTypeEnum)listing.MarketId))
            reason = "unknown market";
        else if (!Enum.IsDefined((TimeFrameEnum)row.TimeFrameId))
            reason = "unknown time frame";
        else if (!TableNameRegistry.TryGetSymbolPart((ExchangeEnum)listing.ExchangeId, (MarketTypeEnum)listing.MarketId, listing.ExchangeSymbol, out _, out var symbolReason))
            reason = $"unusable symbol name: {symbolReason}";
        else
        {
            var series = new CandleSeries((ExchangeEnum)listing.ExchangeId, (MarketTypeEnum)listing.MarketId, new ExchangeSymbol(listing.ExchangeSymbol), (TimeFrameEnum)row.TimeFrameId);
            // Stored as timestamp without a zone, always UTC.
            plan = new CandleSeriesPlan(series, DateTime.SpecifyKind(row.StartDate, DateTimeKind.Utc), row.CollectOpenInterest, row.CollectMarketStatistics);
            reason = string.Empty;
            return true;
        }

        return false;
    }
}
