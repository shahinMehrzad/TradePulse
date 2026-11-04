namespace Trader.Trading;

/// <summary>
/// The limits every order has to pass. They are settings of this program (environment variables / <c>deploy/.env</c>), not something a
/// panel user can raise: whoever stole a session must not be able to lift the caps.
/// </summary>
public sealed class TradingOptions
{
    public const string SectionName = "Trading";

    /// <summary>
    /// Real money. Off until the owner of the server turns it on on purpose; Demo keys trade whatever this says.
    /// </summary>
    public bool AllowLive { get; set; }

    /// <summary>Stops every opening order of every user (closing, reduce-only, orders still go through). Needs a restart to change.</summary>
    public bool Halted { get; set; }

    /// <summary>
    /// Exchange names of the symbols that may ever be traded, e.g. <c>BTCUSDT</c>: the ceiling the owner of the server sets. The panel
    /// chooses among these which are enabled for trading (<c>vault.trading_symbols</c>), and can never go beyond them. There is no default list on purpose: the configuration binder adds a list that is set to a default one, so a default would widen
    /// the owner's ceiling instead of being replaced by it. Without a list nothing can be traded (<c>deploy/docker-compose.yml</c> gives the usual two).
    /// </summary>
    public string[] AllowedSymbols { get; set; } = [];

    /// <summary>The largest single opening order, in USDT (quantity x mark price).</summary>
    public decimal MaxOrderUsd { get; set; } = 100m;

    /// <summary>The most one user may open per UTC day, in USDT. Orders that failed at Binance do not count; orders of unknown outcome do.</summary>
    public decimal MaxDailyUsd { get; set; } = 500m;
}
