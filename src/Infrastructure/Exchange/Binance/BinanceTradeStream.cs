using Application.Exchange;
using Binance.Net.Interfaces.Clients;
using Binance.Net.Objects.Models.Spot.Socket;
using CryptoExchange.Net.Objects.Sockets;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Exchange.Binance;

/// <summary>
/// The aggregated trades of Binance (spot or USD-M futures), pushed over the WebSocket. Binance sells no futures candle below one
/// minute, so anything shorter is built from these. An aggregated trade is the trades of one order at one price and one moment,
/// so what arrives is the price tape without the noise of splitting.
/// </summary>
public sealed class BinanceTradeStream(IBinanceSocketClient client) : ILiveTradeStream
{
    public async Task<ExchangeResult<IAsyncDisposable>> SubscribeAsync(
        ExchangeEnum exchange, MarketTypeEnum marketType, IReadOnlyList<ExchangeSymbol> symbols, ILiveTradeListener listener, CancellationToken cancellationToken)
    {
        if (exchange != ExchangeEnum.Binance)
            throw new ArgumentException($"This stream serves Binance only, not {exchange}.", nameof(exchange));

        var names = symbols.Select(s => s.ToString()).ToList();
        // The stream names a symbol as the exchange does; look it up (any case) among the ones asked for, once, off the callback.
        var known = symbols.Distinct().ToDictionary(s => s.Value, StringComparer.OrdinalIgnoreCase);

        // Socket callback: runs for every trade, so it only converts and hands over. It must stay cheap and must never block.
        void OnMessage(DataEvent<BinanceStreamAggregatedTrade> message)
        {
            if (TryMap(known, message.Data, out var symbol, out var trade))
                listener.OnTrade(symbol, trade);
        }

        var result = marketType switch
        {
            MarketTypeEnum.Futures => await client.UsdFuturesApi.ExchangeData.SubscribeToAggregatedTradeUpdatesAsync(names, OnMessage, cancellationToken),
            MarketTypeEnum.Spot => await client.SpotApi.ExchangeData.SubscribeToAggregatedTradeUpdatesAsync(names, OnMessage, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(marketType), marketType, "Trades are streamed for spot and futures only."),
        };

        return result.Success
            ? ExchangeResult<IAsyncDisposable>.Ok(new BinanceStreamSubscription(client, result.Data, listener))
            : ExchangeResult<IAsyncDisposable>.Fail(result.Error?.Message);
    }

    /// <summary>False for a symbol that was not asked for (nothing to tell the listener about).</summary>
    internal static bool TryMap(
        Dictionary<string, ExchangeSymbol> known, BinanceStreamAggregatedTrade data, out ExchangeSymbol symbol, out LiveTrade trade)
    {
        trade = default;
        if (!known.TryGetValue(data.Symbol, out symbol))
            return false;

        trade = new LiveTrade(DateTime.SpecifyKind(data.TradeTime, DateTimeKind.Utc), data.Price, data.Quantity);
        return true;
    }
}
