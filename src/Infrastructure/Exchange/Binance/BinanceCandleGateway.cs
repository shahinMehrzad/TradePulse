using Application.Exchange;
using Binance.Net.Enums;
using Binance.Net.Interfaces;
using Binance.Net.Interfaces.Clients;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Exchange.Binance;

/// <summary>Candles from Binance's REST API (spot and USD-M futures).</summary>
public sealed class BinanceCandleGateway(IBinanceRestClient client, BinanceRateLimiters rateLimiters) : ICandleGateway
{
    /// <summary>The most candles one request returns. A full page means there may be more.</summary>
    internal const int MaxKlineLimit = 1000;

    public async Task<ExchangeResult<ExchangePage<Candle>>> GetCandlesAsync(CandleSeries series, DateTime from, DateTime? to, CancellationToken cancellationToken)
    {
        // Another exchange's series fetched from Binance would store the wrong prices under a right-looking name.
        if (series.Exchange != ExchangeEnum.Binance)
            throw new ArgumentException($"This gateway serves Binance only, not {series.Exchange}.", nameof(series));

        await rateLimiters.For(series.MarketType).AcquireAsync(GetKlineWeight(series.MarketType, MaxKlineLimit), cancellationToken);

        var symbol = series.Symbol.ToString();
        var interval = (KlineInterval)series.Timeframe;

        // Binance treats endTime as inclusive on open time.
        var result = series.MarketType == MarketTypeEnum.Futures
            ? await client.UsdFuturesApi.ExchangeData.GetKlinesAsync(symbol, interval, startTime: from, endTime: to, limit: MaxKlineLimit, ct: cancellationToken)
            : await client.SpotApi.ExchangeData.GetKlinesAsync(symbol, interval, startTime: from, endTime: to, limit: MaxKlineLimit, ct: cancellationToken);

        if (!result.Success)
            return ExchangeResult<ExchangePage<Candle>>.Fail(result.Error?.Message);

        var candles = result.Data.Select(ToCandle).ToList();
        return ExchangeResult<ExchangePage<Candle>>.Ok(new ExchangePage<Candle>(candles, HasMore: candles.Count >= MaxKlineLimit));
    }

    /// <summary>How much of the per-minute request budget one kline request costs.</summary>
    internal static int GetKlineWeight(MarketTypeEnum market, int limit)
    {
        if (market == MarketTypeEnum.Spot)
            return 2;

        return limit switch
        {
            < 100 => 1,
            < 500 => 2,
            <= 1000 => 5,
            _ => 10,
        };
    }

    private static Candle ToCandle(IBinanceKline kline) => new(
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
}
