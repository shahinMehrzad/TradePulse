using Application.Exchange;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using CryptoExchange.Net.Objects.Sockets;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Exchange.Binance;

/// <summary>Closed candles of Binance spot and USD-M futures, pushed over the kline WebSocket.</summary>
public sealed class BinanceLiveCandleStream(IBinanceSocketClient client) : ILiveCandleStream
{
    public async Task<ExchangeResult<IAsyncDisposable>> SubscribeAsync(
        LiveCandleSubscription subscription, ILiveCandleListener listener, CancellationToken cancellationToken)
    {
        if (subscription.Exchange != ExchangeEnum.Binance)
            throw new ArgumentException($"This stream serves Binance only, not {subscription.Exchange}.", nameof(subscription));

        var symbols = subscription.Symbols.Select(s => s.ToString()).ToList();
        // The stream names a symbol as the exchange does; look it up (any case) among the ones asked for, once, off the callback.
        var known = subscription.Symbols.ToDictionary(s => s.Value, StringComparer.OrdinalIgnoreCase);
        var interval = (KlineInterval)subscription.Timeframe;

        // Socket callback: must stay cheap and must never block.
        void OnMessage(DataEvent<IBinanceStreamKlineData> message) => OnKline(subscription, known, listener, message.Data);

        var result = subscription.MarketType == MarketTypeEnum.Futures
            ? await client.UsdFuturesApi.ExchangeData.SubscribeToKlineUpdatesAsync(symbols, interval, OnMessage, ct: cancellationToken)
            : await client.SpotApi.ExchangeData.SubscribeToKlineUpdatesAsync(symbols, interval, OnMessage, cancellationToken);

        return result.Success
            ? ExchangeResult<IAsyncDisposable>.Ok(new BinanceStreamSubscription(client, result.Data, listener))
            : ExchangeResult<IAsyncDisposable>.Fail(result.Error?.Message);
    }

    private static void OnKline(LiveCandleSubscription subscription, Dictionary<string, ExchangeSymbol> known, ILiveCandleListener listener, IBinanceStreamKlineData message)
    {
        // Every message, closed or not, proves the stream is alive.
        listener.OnMessage();

        var kline = message.Data;
        if (!kline.Final || !known.TryGetValue(message.Symbol, out var symbol))
            return;

        var candle = new Candle(
            openTime: kline.OpenTime,
            openPrice: kline.OpenPrice,
            highPrice: kline.HighPrice,
            lowPrice: kline.LowPrice,
            closePrice: kline.ClosePrice,
            volume: kline.Volume,
            closeTime: kline.CloseTime,
            quoteAssetVolume: kline.QuoteVolume,
            numberOfTrades: kline.TradeCount,
            takerBuyBaseVolume: kline.TakerBuyBaseVolume,
            takerBuyQuoteVolume: kline.TakerBuyQuoteVolume);

        listener.OnClosedCandle(new CandleSeries(ExchangeEnum.Binance, subscription.MarketType, symbol, subscription.Timeframe), candle);
    }
}
