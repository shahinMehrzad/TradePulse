using Application.Exchange;
using Domain.Enums;
using Domain.ValueObjects;
using Domain.Entities;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Exchange;

/// <summary>
/// The one <see cref="ICandleGateway"/> the rest of the program sees: it hands each request to the gateway registered for the exchange of
/// the series (a keyed service, the key is the <see cref="ExchangeEnum"/>). An exchange adds itself with <c>AddKeyedSingleton</c>; nothing
/// here changes.
/// </summary>
public sealed class CandleGatewayRouter(IServiceProvider services) : ICandleGateway
{
    public bool Serves(ExchangeEnum exchange) => services.GetKeyedService<ICandleGateway>(exchange) is not null;

    public Task<ExchangeResult<ExchangePage<Candle>>> GetCandlesAsync(CandleSeries series, DateTime from, DateTime? to, CancellationToken cancellationToken)
    {
        var gateway = services.GetKeyedService<ICandleGateway>(series.Exchange)
            ?? throw new NotSupportedException($"No candle gateway is registered for {series.Exchange}.");

        return gateway.GetCandlesAsync(series, from, to, cancellationToken);
    }
}

/// <summary>The same for <see cref="IExchangeSymbolCatalog"/>: one catalog per exchange, found by the exchange asked about.</summary>
public sealed class ExchangeSymbolCatalogRouter(IServiceProvider services) : IExchangeSymbolCatalog
{
    public bool Serves(ExchangeEnum exchange) => services.GetKeyedService<IExchangeSymbolCatalog>(exchange) is not null;

    public Task<ExchangeResult<ExchangeSymbol?>> FindTradingAsync(
        ExchangeEnum exchange, MarketTypeEnum market, ExchangeSymbol symbol, CancellationToken cancellationToken)
    {
        var catalog = services.GetKeyedService<IExchangeSymbolCatalog>(exchange)
            ?? throw new NotSupportedException($"No symbol catalog is registered for {exchange}.");

        return catalog.FindTradingAsync(exchange, market, symbol, cancellationToken);
    }
}
