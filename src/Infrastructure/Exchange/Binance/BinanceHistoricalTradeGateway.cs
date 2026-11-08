using Application.Exchange;
using Binance.Net.Interfaces.Clients;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Exchange.Binance;

/// <summary>
/// The aggregated trades Binance USD-M futures already made (<c>aggTrades</c> by time): used for the rare minute whose candle holds both the
/// target and the stop of a paper trade, to replay that minute in order. The same kind of trade as the live stream delivers.
/// </summary>
public sealed class BinanceHistoricalTradeGateway(IBinanceRestClient client, BinanceRateLimiters rateLimiters) : IHistoricalTradeGateway
{
    /// <summary>The most trades one request returns. A full page means there may be more in the window.</summary>
    internal const int MaxLimit = 1000;

    /// <summary>The weight Binance charges for one such request.</summary>
    private const int Weight = 20;

    /// <summary>Binance answers a window of at most one hour.</summary>
    private static readonly TimeSpan LongestWindow = TimeSpan.FromHours(1);

    public async Task<ExchangeResult<IReadOnlyList<LiveTrade>>> GetTradesAsync(
        ExchangeEnum exchange, MarketTypeEnum marketType, ExchangeSymbol symbol, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        if (exchange != ExchangeEnum.Binance || marketType != MarketTypeEnum.Futures)
            throw new ArgumentException("This gateway serves Binance futures only.", nameof(exchange));
        if (to <= from || to - from > LongestWindow)
            throw new ArgumentOutOfRangeException(nameof(to), "The window must be longer than nothing and at most one hour.");

        var trades = new List<LiveTrade>();
        var lastId = long.MinValue;
        var start = from;
        var end = to.AddMilliseconds(-1);   // the end of Binance's window is included, ours is not

        while (true)
        {
            await rateLimiters.For(MarketTypeEnum.Futures).AcquireAsync(Weight, cancellationToken);

            var answer = await client.UsdFuturesApi.ExchangeData.GetAggregatedTradeHistoryAsync(
                symbol.ToString(), startTime: start, endTime: end, limit: MaxLimit, ct: cancellationToken);

            if (!answer.Success)
                return ExchangeResult<IReadOnlyList<LiveTrade>>.Fail(answer.Error?.Message);

            var page = answer.Data.OrderBy(t => t.Id).ToList();
            var fresh = page.Where(t => t.Id > lastId).ToList();
            trades.AddRange(fresh.Select(t => new LiveTrade(DateTime.SpecifyKind(t.TradeTime, DateTimeKind.Utc), t.Price, t.Quantity)));

            // A page that is not full is the end of the window.
            if (page.Count < MaxLimit)
                return ExchangeResult<IReadOnlyList<LiveTrade>>.Ok(trades);

            // A full page: the next one starts at the time of the last trade, and the trades already had are skipped by their id.
            if (fresh.Count == 0)
                return ExchangeResult<IReadOnlyList<LiveTrade>>.Fail("More trades than one request returns share one millisecond; the minute cannot be read whole.");

            lastId = page[^1].Id;
            start = page[^1].TradeTime;
        }
    }
}
