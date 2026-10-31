using Application.Exchange;
using Binance.Net.Enums;
using Binance.Net.Interfaces.Clients;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Exchange.Binance;

/// <summary>
/// What the Binance USD-M futures statistics endpoints (<c>/futures/data/...</c>) have in common: the same periods, 500 rows a page
/// and about 30 days of history. A statistic only says which endpoint to call and how a row of it becomes ours.
/// </summary>
public abstract class BinancePeriodStatisticGateway<TRow>(IBinanceRestClient client, BinanceRateLimiters rateLimiters)
    : IPeriodStatisticGateway<TRow>
{
    /// <summary>The most rows one request returns. A full page means there may be more.</summary>
    internal const int MaxLimit = 500;

    /// <summary>Slightly below the documented 30 days so the first request never falls outside the retained window.</summary>
    public TimeSpan MaxHistory { get; } = TimeSpan.FromDays(29);

    protected IBinanceRestClient Client { get; } = client;

    protected abstract string Name { get; }

    /// <summary>
    /// The end of the window one request asks for. Binance returns the NEWEST rows when only a start is given, so a catch-up
    /// that paged by start time alone would skip everything between the start and the latest 500 rows. A window of exactly one page
    /// walks forward from the start instead.
    /// </summary>
    internal static DateTime WindowEnd(DateTime from, TimeFrameEnum timeFrame) => from.AddSeconds((double)(int)timeFrame * MaxLimit);

    /// <summary>One request to the endpoint: the rows it returned, or its error.</summary>
    protected abstract Task<(string? Error, List<TRow> Rows)> FetchAsync(
        string symbol, PeriodInterval period, DateTime from, DateTime to, CancellationToken cancellationToken);

    public async Task<ExchangeResult<ExchangePage<TRow>>> GetAsync(
        ExchangeEnum exchange, ExchangeSymbol symbol, TimeFrameEnum timeFrame, DateTime from, CancellationToken cancellationToken)
    {
        if (exchange != ExchangeEnum.Binance)
            throw new ArgumentException($"This gateway serves Binance only, not {exchange}.", nameof(exchange));

        // Binance's statistics endpoints know fewer periods than there are time frames (no 1s, 1m, 3m, 8h).
        if (!BinancePeriodIntervals.TryMap(timeFrame, out var period))
            throw new ArgumentOutOfRangeException(nameof(timeFrame), timeFrame, $"Binance has no {Name} at this time frame.");

        await rateLimiters.For(MarketTypeEnum.Futures).AcquireAsync(1, cancellationToken);

        var (error, rows) = await FetchAsync(symbol.ToString(), period, from, WindowEnd(from, timeFrame), cancellationToken);
        if (error is not null)
            return ExchangeResult<ExchangePage<TRow>>.Fail(error);

        return ExchangeResult<ExchangePage<TRow>>.Ok(new ExchangePage<TRow>(rows, HasMore: WindowEnd(from, timeFrame) < DateTime.UtcNow));
    }
}

/// <summary>The long/short ratio of all accounts of Binance USD-M futures.</summary>
public sealed class BinanceLongShortRatioGateway(IBinanceRestClient client, BinanceRateLimiters rateLimiters)
    : BinancePeriodStatisticGateway<LongShortRatio>(client, rateLimiters)
{
    protected override string Name => "long/short ratio";

    protected override async Task<(string? Error, List<LongShortRatio> Rows)> FetchAsync(
        string symbol, PeriodInterval period, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var result = await Client.UsdFuturesApi.ExchangeData.GetGlobalLongShortAccountRatioAsync(
            symbol, period, limit: MaxLimit, startTime: from, endTime: to, ct: cancellationToken);
        if (!result.Success)
            return (result.Error?.Message ?? "Unknown error.", []);

        var data = result.Data.ToList();
        var rows = data
            .Where(d => d.Timestamp is not null)
            .Select(d => new LongShortRatio(d.Timestamp!.Value, d.LongShortRatio, d.LongAccount, d.ShortAccount))
            .ToList();

        return (null, rows);
    }
}

/// <summary>The volume bought and sold by takers on Binance USD-M futures.</summary>
public sealed class BinanceTakerVolumeGateway(IBinanceRestClient client, BinanceRateLimiters rateLimiters)
    : BinancePeriodStatisticGateway<TakerVolume>(client, rateLimiters)
{
    protected override string Name => "taker volume";

    protected override async Task<(string? Error, List<TakerVolume> Rows)> FetchAsync(
        string symbol, PeriodInterval period, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        var result = await Client.UsdFuturesApi.ExchangeData.GetTakerBuySellVolumeRatioAsync(
            symbol, period, limit: MaxLimit, startTime: from, endTime: to, ct: cancellationToken);
        if (!result.Success)
            return (result.Error?.Message ?? "Unknown error.", []);

        var data = result.Data.ToList();
        var rows = data
            .Where(d => d.Timestamp is not null)
            .Select(d => new TakerVolume(d.Timestamp!.Value, d.BuySellRatio, d.BuyVolume, d.SellVolume))
            .ToList();

        return (null, rows);
    }
}
