using Domain.Entities;
using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// A series the admin wants collected, the date its history should start from (UTC), and whether its open interest is wanted too.
/// The flag is taken as stored: whether the market can really provide it is checked by whoever collects open interest.
/// </summary>
public readonly record struct CandleSeriesPlan(CandleSeries Series, DateTime StartDate, bool CollectOpenInterest = false)
{
    /// <summary>
    /// Turns a stored configuration row (ids are the enum values) into a plan. False, with the reason, when an id is not a
    /// member of its enum. Whether an exchange is actually supported is the collector's business, not checked here.
    /// </summary>
    public static bool TryCreate(CandleWorkerConfiguration row, out CandleSeriesPlan plan, out string reason)
    {
        plan = default;

        if (!Enum.IsDefined((ExchangeEnum)row.ExchangeId))
            reason = "unknown exchange";
        else if (!Enum.IsDefined((MarketTypeEnum)row.MarketId))
            reason = "unknown market";
        else if (!Enum.IsDefined((SymbolEnum)row.SymbolId))
            reason = "unknown symbol";
        else if (!Enum.IsDefined((TimeFrameEnum)row.TimeFrameId))
            reason = "unknown time frame";
        else
        {
            var series = new CandleSeries((ExchangeEnum)row.ExchangeId, (MarketTypeEnum)row.MarketId, (SymbolEnum)row.SymbolId, (TimeFrameEnum)row.TimeFrameId);
            // Stored as timestamp without a zone, always UTC.
            plan = new CandleSeriesPlan(series, DateTime.SpecifyKind(row.StartDate, DateTimeKind.Utc), row.CollectOpenInterest);
            reason = string.Empty;
            return true;
        }

        return false;
    }
}
