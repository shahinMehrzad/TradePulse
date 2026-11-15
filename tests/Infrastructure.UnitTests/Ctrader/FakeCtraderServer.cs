using System.Net;
using System.Net.Sockets;
using Google.Protobuf;
using Infrastructure.Exchange.Ctrader;
using Infrastructure.Exchange.Ctrader.Protocol;
using Microsoft.Extensions.Options;

namespace Infrastructure.UnitTests.Ctrader;

/// <summary>
/// A stand-in for cTrader's Open API on a real TCP socket (no TLS): it speaks the same framing, signs the application and the account in the way
/// the real one does, and answers whatever else with a handler the test gives. It records what it was sent.
/// </summary>
internal sealed class FakeCtraderServer : IAsyncDisposable
{
    public const string ClientId = "test-client";
    public const string ClientSecret = "test-secret-do-not-log";
    public const string AccessToken = "test-token-do-not-log";
    public const long AccountId = 4242;

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = [];
    private readonly object _gate = new();
    private readonly List<ServerConnection> _open = [];

    public FakeCtraderServer()
    {
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>How many TCP connections were accepted so far.</summary>
    public int Connections { get; private set; }

    /// <summary>Every message received, signing in and heartbeats included, in order.</summary>
    public List<ProtoMessage> Received { get; } = [];

    /// <summary>
    /// Decides the answer to a request that is not a sign in. Returning null leaves it unanswered; the answer's client id is set by the server.
    /// </summary>
    public Func<ProtoMessage, ProtoMessage?> Handler { get; set; } = _ => null;

    /// <summary>When set, the application sign in is answered with this instead of success.</summary>
    public ProtoMessage? ApplicationAuthAnswer { get; set; }

    public CtraderExchangeConfigurations Settings(Action<CtraderExchangeConfigurations>? change = null)
    {
        var settings = new CtraderExchangeConfigurations
        {
            ClientId = ClientId, ClientSecret = ClientSecret, AccessToken = AccessToken, AccountId = AccountId,
            Host = "127.0.0.1", Port = Port, UseTls = false,
            RequestTimeout = TimeSpan.FromSeconds(5), HeartbeatInterval = TimeSpan.FromMinutes(5),
        };
        change?.Invoke(settings);
        return settings;
    }

    public CtraderConnection Connection(Action<CtraderExchangeConfigurations>? change = null) =>
        new(Options.Create(Settings(change)), Microsoft.Extensions.Logging.Abstractions.NullLogger<CtraderConnection>.Instance);

    public static ProtoMessage Answer<T>(ProtoOAPayloadType type, T message) where T : IMessage =>
        new() { PayloadType = (uint)type, Payload = message.ToByteString() };

    /// <summary>Sends a message nobody asked for to every open connection (an event).</summary>
    public async Task PushAsync(ProtoMessage message)
    {
        ServerConnection[] open;
        lock (_gate)
            open = [.. _open];

        foreach (var connection in open)
            await connection.SendAsync(message, _stop.Token);
    }

    /// <summary>Closes every open connection from this side.</summary>
    public void DropConnections()
    {
        lock (_gate)
            foreach (var connection in _open)
                connection.Close();
    }

    public async Task<T> WaitForAsync<T>(Func<T?> probe, string what) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } found)
                return found;

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Waited for {what}.");
    }

    public ProtoMessage[] ReceivedOf(ProtoOAPayloadType type)
    {
        lock (Received)
            return [.. Received.Where(m => m.PayloadType == (uint)type)];
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Connections++;
                var connection = new ServerConnection(client);
                lock (_gate)
                    _open.Add(connection);

                _connections.Add(Task.Run(() => ServeAsync(connection)));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task ServeAsync(ServerConnection connection)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var message = await CtraderFrameCodec.ReadAsync(connection.Stream, _stop.Token);
                if (message is null)
                    break;

                lock (Received)
                    Received.Add(message);

                var answer = message.PayloadType switch
                {
                    (uint)ProtoOAPayloadType.ProtoOaApplicationAuthReq => ApplicationAuthAnswer
                        ?? Answer(ProtoOAPayloadType.ProtoOaApplicationAuthRes, new ProtoOAApplicationAuthRes()),
                    (uint)ProtoOAPayloadType.ProtoOaAccountAuthReq => Answer(ProtoOAPayloadType.ProtoOaAccountAuthRes,
                        new ProtoOAAccountAuthRes { CtidTraderAccountId = AccountId }),
                    (uint)ProtoPayloadType.HeartbeatEvent => null,
                    _ => Handler(message),
                };

                if (answer is null)
                    continue;

                answer.ClientMsgId = message.ClientMsgId;
                await connection.SendAsync(answer, _stop.Token);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        finally
        {
            connection.Close();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        DropConnections();
        await Task.WhenAll(_connections).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        _stop.Dispose();
    }

    private sealed class ServerConnection(TcpClient client)
    {
        private readonly SemaphoreSlim _write = new(1, 1);

        public Stream Stream { get; } = client.GetStream();

        public async Task SendAsync(ProtoMessage message, CancellationToken cancellationToken)
        {
            await _write.WaitAsync(cancellationToken);
            try
            {
                await CtraderFrameCodec.WriteAsync(Stream, message, cancellationToken);
            }
            finally
            {
                _write.Release();
            }
        }

        public void Close()
        {
            try
            {
                client.Close();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}
