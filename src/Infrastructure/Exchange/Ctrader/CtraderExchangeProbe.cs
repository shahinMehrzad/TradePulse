using System.Diagnostics;
using Application.Exchange;
using Domain.Enums;
using Domain.ValueObjects;
using Infrastructure.Exchange.Ctrader.Protocol;

namespace Infrastructure.Exchange.Ctrader;

/// <summary>
/// Whether cTrader can be reached and the application and the account can still sign in. The question is a request for the server's version,
/// which costs nothing, but it goes through the connection, so it signs in first: a token that has expired or was withdrawn, or a connection that
/// broke, shows up here at once instead of when the next candle is due (a daily candle is one request a day).
/// </summary>
internal sealed class CtraderExchangeProbe(ICtraderConnection connection) : IExchangeProbe
{
    public bool Serves(ExchangeEnum exchange) => exchange == ExchangeEnum.Pepperstone;

    public async Task<ExchangeResult<TimeSpan>> PingAsync(ExchangeEnum exchange, MarketTypeEnum market, CancellationToken cancellationToken)
    {
        if (exchange != ExchangeEnum.Pepperstone)
            throw new ArgumentException($"This probe serves Pepperstone only, not {exchange}.", nameof(exchange));

        var started = Stopwatch.GetTimestamp();
        try
        {
            await connection.RequestAsync<ProtoOAVersionRes>(ProtoOAPayloadType.ProtoOaVersionReq, new ProtoOAVersionReq(), historical: false, cancellationToken);
            return ExchangeResult<TimeSpan>.Ok(Stopwatch.GetElapsedTime(started));
        }
        catch (CtraderException ex)
        {
            return ExchangeResult<TimeSpan>.Fail(ex.Message);
        }
    }

    // The connection is the stream, and it has its own heartbeat: there is no separate busy stream to listen to.
    public Task<ExchangeResult<IAsyncDisposable>> SubscribeToProbeStreamAsync(
        ExchangeEnum exchange, ExchangeSymbol symbol, IProbeStreamListener listener, CancellationToken cancellationToken) =>
        Task.FromResult(ExchangeResult<IAsyncDisposable>.Fail("cTrader has no separate probe stream."));
}
