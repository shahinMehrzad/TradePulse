using Application.Exchange;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Exchange.Ctrader;

/// <summary>The symbols the broker lists, asked when the admin names one (CFDs only: a broker has no spot or futures market).</summary>
internal sealed class CtraderSymbolCatalog(CtraderSymbolDirectory symbols) : IExchangeSymbolCatalog
{
    public bool Serves(ExchangeEnum exchange) => exchange == ExchangeEnum.Pepperstone;

    public async Task<ExchangeResult<ExchangeSymbol?>> FindTradingAsync(
        ExchangeEnum exchange, MarketTypeEnum market, ExchangeSymbol symbol, CancellationToken cancellationToken)
    {
        if (exchange != ExchangeEnum.Pepperstone)
            throw new ArgumentException($"This catalog serves Pepperstone only, not {exchange}.", nameof(exchange));

        // The broker has CFDs and nothing else: asking for another market is a plain "no such symbol", not a failed call.
        if (market != MarketTypeEnum.Cfd)
            return ExchangeResult<ExchangeSymbol?>.Ok(null);

        try
        {
            var found = await symbols.FindAsync(symbol, cancellationToken);
            return ExchangeResult<ExchangeSymbol?>.Ok(found is { Enabled: true } ? new ExchangeSymbol(found.Name) : null);
        }
        catch (CtraderException ex)
        {
            return ExchangeResult<ExchangeSymbol?>.Fail(ex.Message);
        }
    }
}
