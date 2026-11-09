namespace Domain.Entities;

/// <summary>
/// A stretch of candles the exchange itself does not have (maintenance, no trades): the collector asked for it several times, the
/// requests went through, and the answers were empty. Rows are only ever added. The program that makes signals reads them to tell a hole
/// that will never fill from one that is only not filled yet.
/// </summary>
public class ConfirmedCandleGap
{
    public const int MaxTableNameLength = 63;

    public long Id { get; set; }

    /// <summary>The candle table the hole is in, as <c>TableNameRegistry</c> names it (e.g. <c>binance_futures_btcusdt_4h</c>), so a reader needs no join.</summary>
    public required string TableName { get; set; }

    /// <summary>UTC. The open time of the first candle the exchange does not have.</summary>
    public DateTime FirstOpenTime { get; set; }

    /// <summary>UTC. The open time of the last candle the exchange does not have.</summary>
    public DateTime LastOpenTime { get; set; }

    /// <summary>UTC.</summary>
    public DateTime ConfirmedAt { get; set; }

    /// <summary>How many complete answers of the exchange (each one empty) this rests on.</summary>
    public int Asks { get; set; }
}
