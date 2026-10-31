using Application.Exchange;
using Binance.Net.Interfaces.Clients;
using Domain.Entities;
using Domain.Enums;
using Domain.ValueObjects;

namespace Infrastructure.Exchange.Binance;

/// <summary>Funding rate settlements of Binance USD-M futures (every 8 h, some symbols 4 h or 1 h).</summary>
public sealed class BinanceFundingRateGateway(IBinanceRestClient client, BinanceRateLimiters rateLimiters) : IFundingRateGateway
{
    /// <summary>The most settlements one request returns. A full page means there may be more.</summary>
    internal const int MaxLimit = 1000;

    public async Task<ExchangeResult<ExchangePage<FundingRate>>> GetFundingRatesAsync(
        ExchangeEnum exchange, ExchangeSymbol symbol, DateTime from, CancellationToken cancellationToken)
    {
        if (exchange != ExchangeEnum.Binance)
            throw new ArgumentException($"This gateway serves Binance only, not {exchange}.", nameof(exchange));

        await rateLimiters.For(MarketTypeEnum.Futures).AcquireAsync(1, cancellationToken);

        var result = await client.UsdFuturesApi.ExchangeData.GetFundingRatesAsync(
            symbol.ToString(), startTime: from, limit: MaxLimit, ct: cancellationToken);

        if (!result.Success)
            return ExchangeResult<ExchangePage<FundingRate>>.Fail(result.Error?.Message);

        var rows = result.Data.Select(r => new FundingRate(r.FundingTime, r.FundingRate, r.MarkPrice ?? 0m)).ToList();
        return ExchangeResult<ExchangePage<FundingRate>>.Ok(new ExchangePage<FundingRate>(rows, HasMore: rows.Count >= MaxLimit));
    }
}
