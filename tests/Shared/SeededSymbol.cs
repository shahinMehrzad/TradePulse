global using TradePulse.TestSupport;
using Domain.ValueObjects;

namespace TradePulse.TestSupport;

/// <summary>
/// The fourteen symbols the first migration lists on Binance Futures, for tests that need a few known ones. The values are the ids
/// they have there, as symbol and as listing. This is test data only: the application reads symbols from the database.
/// </summary>
public enum SeededSymbol
{
    BTCUSDT = 1,
    XAUUSDT = 2,
    ETHUSDT = 3,
    SOLUSDT = 4,
    XRPUSDT = 5,
    BNBUSDT = 6,
    SUIUSDT = 7,
    ADAUSDT = 8,
    DOGEUSDT = 9,
    LINKUSDT = 10,
    LTCUSDT = 11,
    AVAXUSDT = 12,
    DOTUSDT = 13,
    TRXUSDT = 14,
}

public static class SeededSymbols
{
    /// <summary>The name the exchange uses for it.</summary>
    public static ExchangeSymbol Sym(this SeededSymbol symbol) => new(symbol.ToString());
}
