using System.Diagnostics;
using Application.Exchange;
using Binance.Net.Interfaces.Clients;
using Domain.Enums;

namespace Infrastructure.Exchange.Binance;

/// <summary>Reachability checks for Binance: a REST ping per market and a busy futures stream (the mark price).</summary>
public sealed class BinanceExchangeProbe(IBinanceRestClient restClient, IBinanceSocketClient socketClient, BinanceRateLimiters rateLimiters) : IExchangeProbe
{
    /// <summary>How often the mark price stream ticks; the shortest Binance offers.</summary>
    private const int MarkPriceIntervalMs = 3000;

    public async Task<ExchangeResult<TimeSpan>> PingAsync(ExchangeEnum exchange, MarketTypeEnum market, CancellationToken cancellationToken)
    {
        EnsureBinance(exchange);

        // A ping counts against the same per-IP request budget as everything else.
        await rateLimiters.For(market).AcquireAsync(1, cancellationToken);

        var started = Stopwatch.GetTimestamp();
        var result = market == MarketTypeEnum.Futures
            ? await restClient.UsdFuturesApi.ExchangeData.PingAsync(cancellationToken)
            : await restClient.SpotApi.ExchangeData.PingAsync(cancellationToken);

        return result.Success
            ? ExchangeResult<TimeSpan>.Ok(Stopwatch.GetElapsedTime(started))
            : ExchangeResult<TimeSpan>.Fail(result.Error?.Message);
    }

    public async Task<ExchangeResult<IAsyncDisposable>> SubscribeToProbeStreamAsync(
        ExchangeEnum exchange, SymbolEnum symbol, IProbeStreamListener listener, CancellationToken cancellationToken)
    {
        EnsureBinance(exchange);

        var result = await socketClient.UsdFuturesApi.ExchangeData.SubscribeToMarkPriceUpdatesAsync(
            symbol.ToString(), MarkPriceIntervalMs, _ => listener.OnMessage(), cancellationToken);

        return result.Success
            ? ExchangeResult<IAsyncDisposable>.Ok(new BinanceStreamSubscription(socketClient, result.Data, listener))
            : ExchangeResult<IAsyncDisposable>.Fail(result.Error?.Message);
    }

    private static void EnsureBinance(ExchangeEnum exchange)
    {
        if (exchange != ExchangeEnum.Binance)
            throw new ArgumentException($"This probe serves Binance only, not {exchange}.", nameof(exchange));
    }
}
