using Application.Exchange;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Futures.Socket;
using CryptoExchange.Net.Objects.Sockets;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging;
// An alias, because inside this namespace "Binance" means our own namespace, not the library's.
using BinanceOrderSide = Binance.Net.Enums.OrderSide;

namespace Infrastructure.Exchange.Binance;

/// <summary>Forced liquidation orders of Binance USD-M futures, pushed over the WebSocket.</summary>
public sealed class BinanceLiquidationStream(IBinanceSocketClient client, ILogger<BinanceLiquidationStream> logger) : ILiquidationStream
{
    public async Task<ExchangeResult<IAsyncDisposable>> SubscribeAsync(
        ExchangeEnum exchange, IReadOnlyList<SymbolEnum> symbols, ILiquidationListener listener, CancellationToken cancellationToken)
    {
        if (exchange != ExchangeEnum.Binance)
            throw new ArgumentException($"This stream serves Binance only, not {exchange}.", nameof(exchange));

        var names = symbols.Select(s => s.ToString()).ToList();

        // Socket callback: must stay cheap and must never block.
        void OnMessage(DataEvent<BinanceFuturesStreamLiquidation> message) => OnLiquidation(listener, message.Data);

        var result = await client.UsdFuturesApi.ExchangeData.SubscribeToLiquidationUpdatesAsync(names, OnMessage, cancellationToken);

        return result.Success
            ? ExchangeResult<IAsyncDisposable>.Ok(new BinanceStreamSubscription(client, result.Data, listener))
            : ExchangeResult<IAsyncDisposable>.Fail(result.Error?.Message);
    }

    private void OnLiquidation(ILiquidationListener listener, BinanceFuturesStreamLiquidation order)
    {
        if (!Enum.TryParse<SymbolEnum>(order.Symbol, out var symbol) || !Enum.IsDefined(symbol))
        {
            logger.LogWarning("Ignoring liquidation for unknown symbol {Symbol}", order.Symbol);
            return;
        }

        var liquidation = new Liquidation(
            Time: order.Timestamp,
            Side: order.Side == BinanceOrderSide.Buy ? OrderSideEnum.Buy : OrderSideEnum.Sell,
            OrderType: order.Type.ToString(),
            TimeInForce: order.TimeInForce.ToString(),
            Status: order.Status.ToString(),
            Quantity: order.Quantity,
            Price: order.Price,
            AveragePrice: order.AveragePrice,
            LastFilledQuantity: order.LastQuantityFilled,
            AccumulatedQuantity: order.QuantityFilled);

        listener.OnLiquidation(symbol, liquidation);
    }
}
