namespace Application.Research.Watch;

/// <summary>
/// The symbols an administrator has enabled for trading (the trading service keeps them). The watch follows these automatically: a
/// symbol that is traded is a symbol whose sudden moves the admin must hear about. Read only; choosing is done in the trading service.
/// </summary>
public interface ITradedSymbolSource
{
    /// <summary>
    /// The exchange's names (<c>BTCUSDT</c>, upper case) of the symbols enabled for trading on Binance Futures. A failure to read is
    /// thrown, not answered with an empty set: an empty answer would make the watch drop every traded symbol over a hiccup.
    /// </summary>
    Task<IReadOnlySet<string>> GetEnabledAsync(CancellationToken cancellationToken);
}
