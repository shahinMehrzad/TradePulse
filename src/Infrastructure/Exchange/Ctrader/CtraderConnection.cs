using System.Collections.Concurrent;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Google.Protobuf;
using Infrastructure.Exchange.Ctrader.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Exchange.Ctrader;

/// <summary>One request/response conversation with cTrader. The gateways ask through this, so a test can answer instead of a broker.</summary>
public interface ICtraderConnection
{
    /// <summary>
    /// Sends <paramref name="request"/> and waits for its answer. Connects and signs in first when there is no live session, and a session that
    /// broke is replaced by the next call. <paramref name="historical"/> is for the requests cTrader limits harder (candles, ticks).
    /// Throws <see cref="CtraderException"/> when cTrader says no or cannot be reached; cancellation throws as usual.
    /// </summary>
    Task<TResponse> RequestAsync<TResponse>(ProtoOAPayloadType type, IMessage request, bool historical, CancellationToken cancellationToken)
        where TResponse : class, IMessage<TResponse>, new();

    /// <summary>The account the connection is signed in for.</summary>
    long AccountId { get; }
}

/// <summary>
/// The connection to cTrader's Open API: one TCP (TLS) stream, signed in as the application and then as the account. A read loop matches each
/// answer to its request by the id the request carried and hands anything else (events) to <see cref="EventReceived"/>; a heartbeat keeps the
/// stream open while it is quiet. When the stream breaks every waiting request fails at once and the next request makes a new session.
/// </summary>
public sealed class CtraderConnection(IOptions<CtraderExchangeConfigurations> options, ILogger<CtraderConnection> logger, TimeProvider? timeProvider = null)
    : ICtraderConnection, IAsyncDisposable
{
    private readonly CtraderExchangeConfigurations _settings = options.Value;
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _connecting = new(1, 1);
    private readonly RateLimiter _historicalLimit = Limiter(options.Value.HistoricalRequestsPerSecond);
    private readonly RateLimiter _otherLimit = Limiter(options.Value.OtherRequestsPerSecond);
    private Session? _session;
    private bool _disposed;

    public long AccountId => _settings.AccountId ?? 0;

    /// <summary>
    /// A message cTrader sent without being asked (a price, a closed bar). It runs on the read loop: it must be quick and must not throw.
    /// </summary>
    public event Action<ProtoMessage>? EventReceived;

    /// <summary>Raised once when a session ends (the stream broke, cTrader cancelled it, the token was withdrawn), with the reason.</summary>
    public event Action<string>? SessionEnded;

    public async Task<TResponse> RequestAsync<TResponse>(ProtoOAPayloadType type, IMessage request, bool historical, CancellationToken cancellationToken)
        where TResponse : class, IMessage<TResponse>, new()
    {
        var session = await GetSessionAsync(cancellationToken);

        using (var lease = await (historical ? _historicalLimit : _otherLimit).AcquireAsync(1, cancellationToken))
        {
            if (!lease.IsAcquired)
                throw new CtraderException("The request budget of the connection is exhausted.");
        }

        var answer = await session.RequestAsync((uint)type, request, cancellationToken);
        return Parser<TResponse>.Instance.ParseFrom(answer.Payload);
    }

    private async Task<Session> GetSessionAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_session is { IsAlive: true } alive)
            return alive;

        await _connecting.WaitAsync(cancellationToken);
        try
        {
            if (_session is { IsAlive: true } again)
                return again;

            if (!_settings.IsConfigured)
                throw new CtraderException("The cTrader connection is not configured (client id, secret, access token and account id are needed).");

            if (_session is not null)
                await _session.DisposeAsync();

            _session = await OpenSessionAsync(cancellationToken);
            return _session;
        }
        finally
        {
            _connecting.Release();
        }
    }

    private async Task<Session> OpenSessionAsync(CancellationToken cancellationToken)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient { NoDelay = true };
            Stream stream;
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(_settings.RequestTimeout);
                await client.ConnectAsync(_settings.EffectiveHost, _settings.Port, connectTimeout.Token);

                stream = client.GetStream();
                if (_settings.UseTls)
                {
                    var tls = new SslStream(stream);
                    await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _settings.EffectiveHost }, connectTimeout.Token);
                    stream = tls;
                }
            }

            var session = new Session(client, stream, _settings, _time, logger, OnEvent, OnEnded);
            try
            {
                await session.SignInAsync(cancellationToken);
                session.StartHeartbeat();
                return session;
            }
            catch
            {
                await session.DisposeAsync();
                throw;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            client?.Dispose();
            throw new CtraderException("Connecting to cTrader took too long.");
        }
        catch (Exception ex) when (ex is not CtraderException && ex is not OperationCanceledException)
        {
            client?.Dispose();

            // Only the kind of failure: an exception's text can hold the address.
            throw new CtraderException($"Cannot reach cTrader ({ex.GetType().Name}).", inner: ex);
        }
    }

    private void OnEvent(ProtoMessage message) => EventReceived?.Invoke(message);

    private void OnEnded(string reason) => SessionEnded?.Invoke(reason);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_session is not null)
            await _session.DisposeAsync();

        _historicalLimit.Dispose();
        _otherLimit.Dispose();
        _connecting.Dispose();
    }

    private static RateLimiter Limiter(int perSecond) => new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
    {
        PermitLimit = Math.Max(1, perSecond),
        Window = TimeSpan.FromSeconds(1),
        SegmentsPerWindow = 10,
        QueueLimit = int.MaxValue,
        AutoReplenishment = true,
    });

    private static class Parser<T> where T : class, IMessage<T>, new()
    {
        public static readonly MessageParser<T> Instance = new(() => new T());
    }

    /// <summary>One TCP stream and what runs on it. It lives until the stream breaks, then it is thrown away whole.</summary>
    private sealed class Session : IAsyncDisposable
    {
        private readonly TcpClient _client;
        private readonly Stream _stream;
        private readonly CtraderExchangeConfigurations _settings;
        private readonly TimeProvider _time;
        private readonly ILogger _logger;
        private readonly Action<ProtoMessage> _onEvent;
        private readonly Action<string> _onEnded;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _write = new(1, 1);
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ProtoMessage>> _pending = new();
        private readonly Task _readLoop;
        private Task _heartbeat = Task.CompletedTask;
        private int _ended;

        public Session(TcpClient client, Stream stream, CtraderExchangeConfigurations settings, TimeProvider time, ILogger logger,
            Action<ProtoMessage> onEvent, Action<string> onEnded)
        {
            _client = client;
            _stream = stream;
            _settings = settings;
            _time = time;
            _logger = logger;
            _onEvent = onEvent;
            _onEnded = onEnded;
            _readLoop = Task.Run(ReadLoopAsync);
        }

        public bool IsAlive => Volatile.Read(ref _ended) == 0;

        public async Task SignInAsync(CancellationToken cancellationToken)
        {
            var app = await RequestAsync((uint)ProtoOAPayloadType.ProtoOaApplicationAuthReq,
                new ProtoOAApplicationAuthReq { ClientId = _settings.ClientId, ClientSecret = _settings.ClientSecret }, cancellationToken);
            Expect(app, ProtoOAPayloadType.ProtoOaApplicationAuthRes);

            var account = await RequestAsync((uint)ProtoOAPayloadType.ProtoOaAccountAuthReq,
                new ProtoOAAccountAuthReq { CtidTraderAccountId = _settings.AccountId ?? 0, AccessToken = _settings.AccessToken }, cancellationToken);
            Expect(account, ProtoOAPayloadType.ProtoOaAccountAuthRes);
        }

        public void StartHeartbeat() => _heartbeat = Task.Run(HeartbeatLoopAsync);

        public async Task<ProtoMessage> RequestAsync(uint payloadType, IMessage payload, CancellationToken cancellationToken)
        {
            if (!IsAlive)
                throw new CtraderException("The cTrader session has ended.");

            var id = Guid.NewGuid().ToString("N");
            var waiting = new TaskCompletionSource<ProtoMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = waiting;

            try
            {
                await SendAsync(new ProtoMessage { PayloadType = payloadType, Payload = payload.ToByteString(), ClientMsgId = id }, cancellationToken);
                var answer = await waiting.Task.WaitAsync(_settings.RequestTimeout, _time, cancellationToken);
                return ThrowIfError(answer);
            }
            catch (TimeoutException)
            {
                throw new CtraderException("cTrader did not answer in time.");
            }
            finally
            {
                _pending.TryRemove(id, out _);
            }
        }

        private async Task SendAsync(ProtoMessage message, CancellationToken cancellationToken)
        {
            await _write.WaitAsync(cancellationToken);
            try
            {
                await CtraderFrameCodec.WriteAsync(_stream, message, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                End($"The connection broke while sending ({ex.GetType().Name}).");
                throw new CtraderException("The connection to cTrader broke.", inner: ex);
            }
            finally
            {
                _write.Release();
            }
        }

        private async Task ReadLoopAsync()
        {
            var reason = "The connection to cTrader was closed.";
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var message = await CtraderFrameCodec.ReadAsync(_stream, _stop.Token);
                    if (message is null)
                        break;

                    if (message.PayloadType == (uint)ProtoPayloadType.HeartbeatEvent)
                        continue;

                    if (!string.IsNullOrEmpty(message.ClientMsgId) && _pending.TryRemove(message.ClientMsgId, out var waiting))
                    {
                        waiting.TrySetResult(message);
                        continue;
                    }

                    if (IsEndOfSession(message, out var why))
                    {
                        reason = why;
                        break;
                    }

                    try
                    {
                        _onEvent(message);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "A cTrader event handler failed (payload type {PayloadType})", message.PayloadType);
                    }
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
            }
            catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException or ObjectDisposedException or InvalidProtocolBufferException)
            {
                reason = $"The connection to cTrader broke ({ex.GetType().Name}).";
            }

            End(reason);
        }

        private async Task HeartbeatLoopAsync()
        {
            using var timer = new PeriodicTimer(_settings.HeartbeatInterval, _time);
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token))
                    await SendAsync(new ProtoMessage { PayloadType = (uint)ProtoPayloadType.HeartbeatEvent, Payload = new ProtoHeartbeatEvent().ToByteString() }, _stop.Token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or CtraderException)
            {
                // The session ended; End has already told everyone.
            }
        }

        private static bool IsEndOfSession(ProtoMessage message, out string reason)
        {
            reason = message.PayloadType switch
            {
                (uint)ProtoOAPayloadType.ProtoOaClientDisconnectEvent => "cTrader cancelled the connection of the application.",
                (uint)ProtoOAPayloadType.ProtoOaAccountsTokenInvalidatedEvent => "The access token was withdrawn or has expired.",
                (uint)ProtoOAPayloadType.ProtoOaAccountDisconnectEvent => "cTrader disconnected the account.",
                _ => string.Empty,
            };

            return reason.Length > 0;
        }

        private static ProtoMessage ThrowIfError(ProtoMessage answer)
        {
            if (answer.PayloadType == (uint)ProtoOAPayloadType.ProtoOaErrorRes)
            {
                var error = ProtoOAErrorRes.Parser.ParseFrom(answer.Payload);
                throw new CtraderException(Describe(error.ErrorCode, error.Description), error.ErrorCode,
                    error.HasRetryAfter ? TimeSpan.FromSeconds(error.RetryAfter) : null);
            }

            if (answer.PayloadType == (uint)ProtoPayloadType.ErrorRes)
            {
                var error = ProtoErrorRes.Parser.ParseFrom(answer.Payload);
                throw new CtraderException(Describe(error.ErrorCode, error.Description), error.ErrorCode);
            }

            return answer;
        }

        private static void Expect(ProtoMessage answer, ProtoOAPayloadType expected)
        {
            if (answer.PayloadType != (uint)expected)
                throw new CtraderException($"cTrader answered with payload type {answer.PayloadType} instead of {(uint)expected}.");
        }

        // The description is cTrader's own text about the error, and so far holds no secret of ours.
        private static string Describe(string code, string? description) =>
            string.IsNullOrWhiteSpace(description) ? $"cTrader refused the request ({code})." : $"cTrader refused the request ({code}): {description}";

        /// <summary>Ends the session once: everything waiting fails now, and the stream is closed.</summary>
        private void End(string reason)
        {
            if (Interlocked.Exchange(ref _ended, 1) != 0)
                return;

            _stop.Cancel();
            foreach (var (id, waiting) in _pending)
                if (_pending.TryRemove(id, out _))
                    waiting.TrySetException(new CtraderException(reason));

            try
            {
                _client.Close();
            }
            catch (ObjectDisposedException)
            {
            }

            _logger.LogWarning("The cTrader session ended: {Reason}", reason);
            _onEnded(reason);
        }

        public async ValueTask DisposeAsync()
        {
            End("The session was closed by this program.");
            await Task.WhenAll(_readLoop, _heartbeat).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            _stop.Dispose();
            _write.Dispose();
            _client.Dispose();
        }
    }
}
