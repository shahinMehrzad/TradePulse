using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Exchange;

/// <summary>
/// What an exchange itself lists. Used when the admin names a symbol for an exchange market, so a typo or a delisted symbol is
/// refused at once instead of failing in the collector later.
/// </summary>
public interface IExchangeSymbolCatalog
{
    /// <summary>
    /// Whether the exchange can be asked at all. An exchange without a catalog must be refused when a symbol is listed: nothing would check the
    /// name. The default (every exchange) is for a catalog of a single exchange.
    /// </summary>
    bool Serves(ExchangeEnum exchange) => true;

    /// <summary>
    /// The name exactly as the exchange spells it when <paramref name="symbol"/> (in any case) exists on <paramref name="market"/>
    /// of the exchange and is trading, otherwise null. A failed call is a result, not an exception: the caller must not guess the answer.
    /// </summary>
    Task<ExchangeResult<ExchangeSymbol?>> FindTradingAsync(ExchangeEnum exchange, MarketTypeEnum market, ExchangeSymbol symbol, CancellationToken cancellationToken);
}
