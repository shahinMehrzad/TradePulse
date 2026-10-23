using Domain.Enums;
using Domain.Services;

namespace Domain.ValueObjects;

/// <summary>
/// Identifies one candle series on any exchange. The table name is derived from it, never taken from a caller,
/// so it is safe to interpolate into SQL.
/// </summary>
public readonly record struct CandleSeries(ExchangeEnum Exchange, MarketTypeEnum MarketType, SymbolEnum Symbol, TimeFrameEnum Timeframe)
{
    /// <summary>The candle table of this series (see <see cref="TableNameRegistry"/>).</summary>
    public string TableName => TableNameRegistry.GetCandleTableName(Exchange, MarketType, Symbol, Timeframe);

    /// <summary>The length of one candle.</summary>
    public TimeSpan Interval => TimeSpan.FromSeconds((int)Timeframe);
}
