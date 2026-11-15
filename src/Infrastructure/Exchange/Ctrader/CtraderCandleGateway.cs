using Application.Exchange;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Exchange.Ctrader.Protocol;

namespace Infrastructure.Exchange.Ctrader;

/// <summary>
/// Candles (cTrader calls them trendbars) of a broker's CFDs through the Open API. cTrader answers in whole stretches of time, not in pages of a
/// fixed size, and a stretch can hold no bar (a weekend, a holiday, a symbol that did not exist yet): the gateway moves on to the next stretch
/// until it finds bars or reaches the end, because the worker takes an empty page for "nothing more".
/// </summary>
internal sealed class CtraderCandleGateway(ICtraderConnection connection, CtraderSymbolDirectory symbols, TimeProvider time) : ICandleGateway
{
    private const decimal PriceScale = 100_000m;

    public bool Serves(ExchangeEnum exchange) => exchange == ExchangeEnum.Pepperstone;

    public async Task<ExchangeResult<ExchangePage<Candle>>> GetCandlesAsync(CandleSeries series, DateTime from, DateTime? to, CancellationToken cancellationToken)
    {
        // Another exchange's series fetched from here would store wrong prices under a right-looking name.
        if (series.Exchange != ExchangeEnum.Pepperstone || series.MarketType != MarketTypeEnum.Cfd)
            throw new ArgumentException($"This gateway serves Pepperstone CFDs only, not {series.Exchange} {series.MarketType}.", nameof(series));
        if (!CtraderTimeFrames.TryGetPeriod(series.Timeframe, out var period))
            throw new ArgumentOutOfRangeException(nameof(series), series.Timeframe, "cTrader has no trendbar period for this time frame.");

        try
        {
            var symbol = await symbols.FindAsync(series.Symbol, cancellationToken);
            if (symbol is null)
                return ExchangeResult<ExchangePage<Candle>>.Fail($"cTrader has no symbol {series.Symbol}.");

            var digits = await symbols.GetDigitsAsync(symbol, cancellationToken);
            var end = to ?? time.GetUtcNow().UtcDateTime;
            var span = CtraderTimeFrames.MaxRange(period);

            for (var start = from; start <= end;)
            {
                var stop = start + span < end ? start + span : end;
                var answer = await connection.RequestAsync<ProtoOAGetTrendbarsRes>(ProtoOAPayloadType.ProtoOaGetTrendbarsReq, new ProtoOAGetTrendbarsReq
                {
                    CtidTraderAccountId = connection.AccountId,
                    SymbolId = symbol.Id,
                    Period = period,
                    FromTimestamp = new DateTimeOffset(start, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                    ToTimestamp = new DateTimeOffset(stop, TimeSpan.Zero).ToUnixTimeMilliseconds(),
                }, historical: true, cancellationToken);

                var candles = answer.Trendbar.Select(bar => ToCandle(bar, series.Interval, digits))
                    .Where(c => c.TimeRange.OpenTime >= from && c.TimeRange.OpenTime <= end)
                    .OrderBy(c => c.TimeRange.OpenTime)
                    .ToList();

                if (candles.Count > 0)
                    return ExchangeResult<ExchangePage<Candle>>.Ok(new ExchangePage<Candle>(candles, HasMore: answer.HasMore || stop < end));

                // An answer with no bar in it: the next stretch.
                start = stop.AddMilliseconds(1);
            }

            return ExchangeResult<ExchangePage<Candle>>.Ok(new ExchangePage<Candle>([], HasMore: false));
        }
        catch (CtraderException ex)
        {
            return ExchangeResult<ExchangePage<Candle>>.Fail(ex.Message);
        }
    }

    // The relative encoding: low is the price (times 100000), the others are distances above it.
    private static Candle ToCandle(ProtoOATrendbar bar, TimeSpan interval, int digits)
    {
        var open = DateTimeOffset.FromUnixTimeSeconds((long)bar.UtcTimestampInMinutes * 60).UtcDateTime;

        return new Candle(
            openTime: open,
            openPrice: Price(bar.Low, (long)bar.DeltaOpen, digits),
            highPrice: Price(bar.Low, (long)bar.DeltaHigh, digits),
            lowPrice: Price(bar.Low, 0, digits),
            closePrice: Price(bar.Low, (long)bar.DeltaClose, digits),
            volume: bar.Volume,
            closeTime: open + interval - TimeSpan.FromMilliseconds(1),
            // A broker has no order book: cTrader's volume is the number of price ticks, and there are no trades or taker sides to count.
            quoteAssetVolume: 0,
            numberOfTrades: 0,
            takerBuyBaseVolume: 0,
            takerBuyQuoteVolume: 0);
    }

    private static decimal Price(long low, long delta, int digits) => Math.Round((low + delta) / PriceScale, digits);
}
