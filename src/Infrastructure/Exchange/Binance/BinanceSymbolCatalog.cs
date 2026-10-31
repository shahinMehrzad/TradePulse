using Application.Exchange;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Exchange.Binance;

/// <summary>
/// The symbols Binance lists on a market, asked when the admin names one. The whole list is read and searched (rather than asking
/// for the one symbol) so that "Binance says there is no such symbol" cannot be mistaken for "the call failed". It runs only on
/// that rare admin action, so it does not draw from the collector's request budget.
/// </summary>
public sealed class BinanceSymbolCatalog(IBinanceRestClient client) : IExchangeSymbolCatalog
{
    public async Task<ExchangeResult<ExchangeSymbol?>> FindTradingAsync(
        ExchangeEnum exchange, MarketTypeEnum market, ExchangeSymbol symbol, CancellationToken cancellationToken)
    {
        if (exchange != ExchangeEnum.Binance)
            throw new ArgumentException($"This catalog serves Binance only, not {exchange}.", nameof(exchange));

        if (market == MarketTypeEnum.Futures)
        {
            var futures = await client.UsdFuturesApi.ExchangeData.GetExchangeInfoAsync(cancellationToken);
            return futures.Success
                ? ExchangeResult<ExchangeSymbol?>.Ok(Find(futures.Data.Symbols.Where(s => s.Status == SymbolStatus.Trading).Select(s => s.Name), symbol))
                : ExchangeResult<ExchangeSymbol?>.Fail(futures.Error?.Message);
        }

        var spot = await client.SpotApi.ExchangeData.GetExchangeInfoAsync(ct: cancellationToken);
        return spot.Success
            ? ExchangeResult<ExchangeSymbol?>.Ok(Find(spot.Data.Symbols.Where(s => s.Status == SymbolStatus.Trading).Select(s => s.Name), symbol))
            : ExchangeResult<ExchangeSymbol?>.Fail(spot.Error?.Message);
    }

    // Binance names are upper case; the admin may type them in any case.
    private static ExchangeSymbol? Find(IEnumerable<string> trading, ExchangeSymbol symbol) =>
        trading.FirstOrDefault(name => string.Equals(name, symbol.Value, StringComparison.OrdinalIgnoreCase)) is { } found ? new ExchangeSymbol(found) : null;
}
