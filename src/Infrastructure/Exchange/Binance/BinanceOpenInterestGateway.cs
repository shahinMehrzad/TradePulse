using Application.Exchange;
using Binance.Net.Interfaces.Clients;
using Domain.Entities;
using Domain.Enums;

namespace Infrastructure.Exchange.Binance;

/// <summary>Open interest history of Binance USD-M futures.</summary>
public sealed class BinanceOpenInterestGateway(IBinanceRestClient client, BinanceRateLimiters rateLimiters) : IOpenInterestGateway
{
    /// <summary>The most snapshots one request returns. A full page means there may be more.</summary>
    internal const int MaxLimit = 500;

    /// <summary>Slightly below the documented 30 days so the first request never falls outside the retained window.</summary>
    public TimeSpan MaxHistory { get; } = TimeSpan.FromDays(29);

    public async Task<ExchangeResult<ExchangePage<OpenInterest>>> GetOpenInterestAsync(
        ExchangeEnum exchange, SymbolEnum symbol, TimeFrameEnum timeFrame, DateTime from, CancellationToken cancellationToken)
    {
        if (exchange != ExchangeEnum.Binance)
            throw new ArgumentException($"This gateway serves Binance only, not {exchange}.", nameof(exchange));

        // Binance's statistics endpoints know fewer periods than there are time frames (no 1s, 1m, 3m, 8h).
        if (!BinancePeriodIntervals.TryMap(timeFrame, out var period))
            throw new ArgumentOutOfRangeException(nameof(timeFrame), timeFrame, "Binance has no open interest at this time frame.");

        await rateLimiters.For(MarketTypeEnum.Futures).AcquireAsync(1, cancellationToken);

        // Binance returns the NEWEST rows when only a start is given, so ask for a window of one page from the start.
        var to = from.AddSeconds((double)(int)timeFrame * MaxLimit);
        var result = await client.UsdFuturesApi.ExchangeData.GetOpenInterestHistoryAsync(
            symbol.ToString(), period, limit: MaxLimit, startTime: from, endTime: to, ct: cancellationToken);

        if (!result.Success)
            return ExchangeResult<ExchangePage<OpenInterest>>.Fail(result.Error?.Message);

        var data = result.Data.ToList();
        var rows = data
            .Where(r => r.Timestamp.HasValue)
            .Select(r => new OpenInterest(r.Timestamp!.Value, r.SumOpenInterest, r.SumOpenInterestValue))
            .ToList();

        return ExchangeResult<ExchangePage<OpenInterest>>.Ok(new ExchangePage<OpenInterest>(rows, HasMore: to < DateTime.UtcNow));
    }
}
