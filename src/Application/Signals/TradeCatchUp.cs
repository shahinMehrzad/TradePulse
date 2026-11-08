using Application.Exchange;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace Application.Signals;

/// <summary>
/// Looks at the stretch of price nobody was watching (the service was stopped, a connection was lost, the queue was full) through the 1 minute
/// candles of the exchange, and ends the trades that reached their target or stop in it. Nothing is guessed:
/// <list type="bullet">
/// <item>a candle that tells the order (one level reached, or opened beyond one) ends the trade;</item>
/// <item>a candle that holds both levels does not: the trades of that minute are asked for and replayed in order;</item>
/// <item>when the exchange does not answer, the trade is left open, at the point it had reached, and the caller asks again later.</item>
/// </list>
/// Only closed minutes are used; the minute in progress is the live trades' job.
/// </summary>
public sealed class TradeCatchUp(ICandleGateway candles, IHistoricalTradeGateway history, TimeProvider time, ILogger<TradeCatchUp> logger)
{
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Looks at the past of the open <paramref name="trades"/> of one symbol, from <paramref name="from"/> or, when it is null, from where each
    /// had been followed (<see cref="PaperTrade.LastCheckedAt"/>). Trades that end are added to <paramref name="ended"/>.
    /// </summary>
    /// <returns>True when every trade was looked at up to the last closed minute. False when the exchange did not answer: ask again.</returns>
    public async Task<bool> RunAsync(FollowedSymbol symbol, IReadOnlyList<PaperTrade> trades, DateTime? from, List<PaperTrade> ended, CancellationToken cancellationToken)
    {
        var live = trades.Where(t => t.IsOpen).ToList();
        if (live.Count == 0)
            return true;

        var lastClosedEnd = Floor(time.GetUtcNow().UtcDateTime);   // the minutes before this one are closed
        var starts = live.ToDictionary(t => t, t => Floor(from ?? t.LastCheckedAt ?? t.OpenedAt));
        var stalled = new HashSet<PaperTrade>();
        var series = new CandleSeries(symbol.Exchange, symbol.Market, symbol.ExchangeSymbol, TimeFrameEnum._1M);

        var cursor = starts.Values.Min();
        while (cursor < lastClosedEnd && live.Any(t => t.IsOpen && !stalled.Contains(t)))
        {
            var answer = await candles.GetCandlesAsync(series, cursor, lastClosedEnd - Minute, cancellationToken);
            if (!answer.Success)
            {
                logger.LogWarning("{Symbol}: the 1 minute candles from {From:u} could not be read ({Error}). The trades stay open and it is asked again",
                    symbol.Symbol, cursor, answer.Error);
                return false;
            }

            var page = answer.Value;
            foreach (var candle in page.Items.OrderBy(c => c.TimeRange.OpenTime))
            {
                var openTime = candle.TimeRange.OpenTime;
                if (openTime >= lastClosedEnd)
                    break;

                var closeTime = openTime + Minute;
                foreach (var trade in live)
                {
                    if (!trade.IsOpen || stalled.Contains(trade) || openTime < starts[trade])
                        continue;

                    var verdict = trade.ObserveCandle(openTime, candle.Open, candle.High, candle.Low, candle.Close, closeTime);
                    if (verdict == CandleVerdictEnum.NeedsTrades && !await ReplayAsync(symbol, trade, openTime, cancellationToken))
                    {
                        stalled.Add(trade);   // it stays where it was: not a single later candle may be looked at before this minute is known
                        continue;
                    }

                    if (trade.IsOpen)
                        trade.MarkChecked(closeTime);
                    else
                        ended.Add(trade);
                }
            }

            if (!page.HasMore || page.Items.Count == 0)
                break;

            cursor = page.Items.Max(c => c.TimeRange.OpenTime) + Minute;
        }

        // Every minute up to the last closed one was answered; a minute the exchange has no candle for had no trade, so the price did not move.
        foreach (var trade in live.Where(t => t.IsOpen && !stalled.Contains(t)))
            trade.MarkChecked(lastClosedEnd);

        return stalled.Count == 0;
    }

    /// <summary>The trades of one minute, replayed in the order they happened. False when they could not be had.</summary>
    private async Task<bool> ReplayAsync(FollowedSymbol symbol, PaperTrade trade, DateTime minute, CancellationToken cancellationToken)
    {
        var answer = await history.GetTradesAsync(symbol.Exchange, symbol.Market, symbol.ExchangeSymbol, minute, minute + Minute, cancellationToken);
        if (!answer.Success)
        {
            logger.LogWarning("{Symbol}: the minute {Minute:u} holds both the target and the stop of a trade, and its trades could not be read ({Error}). The trade stays open and it is asked again",
                symbol.Symbol, minute, answer.Error);
            return false;
        }

        foreach (var one in answer.Value.OrderBy(t => t.Time))
        {
            if (trade.Observe(one.Price, one.Time))
                break;
        }

        return true;
    }

    private static DateTime Floor(DateTime time) => new(time.Ticks - time.Ticks % Minute.Ticks, DateTimeKind.Utc);
}
