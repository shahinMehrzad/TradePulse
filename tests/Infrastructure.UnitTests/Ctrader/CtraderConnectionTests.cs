using Google.Protobuf;
using Infrastructure.Exchange.Ctrader;
using Infrastructure.Exchange.Ctrader.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.UnitTests.Ctrader;

/// <summary>The real connection against a stand-in for cTrader on a real socket.</summary>
public class CtraderConnectionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<ProtoOASymbolsListRes> AskForSymbolsAsync(CtraderConnection connection) =>
        connection.RequestAsync<ProtoOASymbolsListRes>(ProtoOAPayloadType.ProtoOaSymbolsListReq,
            new ProtoOASymbolsListReq { CtidTraderAccountId = FakeCtraderServer.AccountId }, historical: false, Ct);

    private static ProtoMessage SymbolsAnswer(params string[] names) => FakeCtraderServer.Answer(ProtoOAPayloadType.ProtoOaSymbolsListRes,
        new ProtoOASymbolsListRes
        {
            CtidTraderAccountId = FakeCtraderServer.AccountId,
            Symbol = { names.Select((name, i) => new ProtoOALightSymbol { SymbolId = i + 1, SymbolName = name }) },
        });

    [Fact]
    public async Task ItSignsTheApplicationInAndThenTheAccountBeforeTheFirstRequest()
    {
        await using var server = new FakeCtraderServer { Handler = _ => SymbolsAnswer("EURUSD") };
        await using var connection = server.Connection();

        var answer = await AskForSymbolsAsync(connection);

        Assert.Equal("EURUSD", answer.Symbol.Single().SymbolName);
        Assert.Equal(
            [(uint)ProtoOAPayloadType.ProtoOaApplicationAuthReq, (uint)ProtoOAPayloadType.ProtoOaAccountAuthReq, (uint)ProtoOAPayloadType.ProtoOaSymbolsListReq],
            server.Received.Select(m => m.PayloadType));

        var app = ProtoOAApplicationAuthReq.Parser.ParseFrom(server.ReceivedOf(ProtoOAPayloadType.ProtoOaApplicationAuthReq).Single().Payload);
        Assert.Equal(FakeCtraderServer.ClientId, app.ClientId);
        Assert.Equal(FakeCtraderServer.ClientSecret, app.ClientSecret);

        var account = ProtoOAAccountAuthReq.Parser.ParseFrom(server.ReceivedOf(ProtoOAPayloadType.ProtoOaAccountAuthReq).Single().Payload);
        Assert.Equal(FakeCtraderServer.AccountId, account.CtidTraderAccountId);
        Assert.Equal(FakeCtraderServer.AccessToken, account.AccessToken);
    }

    [Fact]
    public async Task ManyRequestsAtOnceShareOneSessionAndEachGetsItsOwnAnswer()
    {
        await using var server = new FakeCtraderServer
        {
            // The answer says which account was asked about by echoing the request's include flag as a symbol name.
            Handler = message => SymbolsAnswer(ProtoOASymbolsListReq.Parser.ParseFrom(message.Payload).IncludeArchivedSymbols ? "archived" : "plain"),
        };
        await using var connection = server.Connection();

        var calls = Enumerable.Range(0, 20).Select(i => connection.RequestAsync<ProtoOASymbolsListRes>(
            ProtoOAPayloadType.ProtoOaSymbolsListReq,
            new ProtoOASymbolsListReq { CtidTraderAccountId = FakeCtraderServer.AccountId, IncludeArchivedSymbols = i % 2 == 0 }, false, Ct)).ToArray();
        var answers = await Task.WhenAll(calls);

        Assert.Equal(1, server.Connections);
        Assert.Equal(20, answers.Length);
        Assert.All(answers.Select((a, i) => (a, i)), x => Assert.Equal(x.i % 2 == 0 ? "archived" : "plain", x.a.Symbol.Single().SymbolName));
    }

    [Fact]
    public async Task AnErrorAnswerBecomesAnExceptionWithCTradersCodeAndRetryAfter()
    {
        await using var server = new FakeCtraderServer
        {
            Handler = _ => FakeCtraderServer.Answer(ProtoOAPayloadType.ProtoOaErrorRes,
                new ProtoOAErrorRes { ErrorCode = "BLOCKED_PAYLOAD_TYPE", Description = "slow down", RetryAfter = 12 }),
        };
        await using var connection = server.Connection();

        var failure = await Assert.ThrowsAsync<CtraderException>(() => AskForSymbolsAsync(connection));

        Assert.Equal("BLOCKED_PAYLOAD_TYPE", failure.ErrorCode);
        Assert.Equal(TimeSpan.FromSeconds(12), failure.RetryAfter);
        Assert.Contains("slow down", failure.Message);
    }

    [Fact]
    public async Task ARefusedApplicationSignInIsAnExceptionAndNoRequestIsSent()
    {
        await using var server = new FakeCtraderServer
        {
            ApplicationAuthAnswer = FakeCtraderServer.Answer(ProtoOAPayloadType.ProtoOaErrorRes,
                new ProtoOAErrorRes { ErrorCode = "CH_CLIENT_AUTH_FAILURE", Description = "bad client" }),
        };
        await using var connection = server.Connection();

        var failure = await Assert.ThrowsAsync<CtraderException>(() => AskForSymbolsAsync(connection));

        Assert.Equal("CH_CLIENT_AUTH_FAILURE", failure.ErrorCode);
        Assert.Empty(server.ReceivedOf(ProtoOAPayloadType.ProtoOaSymbolsListReq));
        Assert.Empty(server.ReceivedOf(ProtoOAPayloadType.ProtoOaAccountAuthReq));
    }

    [Fact]
    public async Task AnUnansweredRequestTimesOutAndTheSessionStaysUsable()
    {
        var silent = true;
        await using var server = new FakeCtraderServer { Handler = _ => silent ? null : SymbolsAnswer("EURUSD") };
        await using var connection = server.Connection(s => s.RequestTimeout = TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAsync<CtraderException>(() => AskForSymbolsAsync(connection));
        silent = false;

        Assert.Equal("EURUSD", (await AskForSymbolsAsync(connection)).Symbol.Single().SymbolName);
        Assert.Equal(1, server.Connections);
    }

    [Fact]
    public async Task WhenTheServerDropsTheConnectionWaitingRequestsFailAtOnceAndTheNextOneSignsInAgain()
    {
        var silent = true;
        await using var server = new FakeCtraderServer { Handler = _ => silent ? null : SymbolsAnswer("EURUSD") };
        await using var connection = server.Connection();
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SessionEnded += reason => ended.TrySetResult(reason);

        var waiting = AskForSymbolsAsync(connection);
        await server.WaitForAsync(() => server.ReceivedOf(ProtoOAPayloadType.ProtoOaSymbolsListReq).FirstOrDefault(), "the request");
        server.DropConnections();

        await Assert.ThrowsAsync<CtraderException>(() => waiting);
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        silent = false;
        Assert.Equal("EURUSD", (await AskForSymbolsAsync(connection)).Symbol.Single().SymbolName);
        Assert.Equal(2, server.Connections);
        Assert.Equal(2, server.ReceivedOf(ProtoOAPayloadType.ProtoOaAccountAuthReq).Length);
    }

    [Fact]
    public async Task AWithdrawnTokenEndsTheSessionAndIsNotPassedOnAsAnEvent()
    {
        await using var server = new FakeCtraderServer { Handler = _ => SymbolsAnswer("EURUSD") };
        await using var connection = server.Connection();
        var events = new List<ProtoMessage>();
        connection.EventReceived += events.Add;
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.SessionEnded += reason => ended.TrySetResult(reason);
        await AskForSymbolsAsync(connection);

        await server.PushAsync(FakeCtraderServer.Answer(ProtoOAPayloadType.ProtoOaAccountsTokenInvalidatedEvent, new ProtoOAAccountsTokenInvalidatedEvent()));

        Assert.Contains("token", await ended.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct));
        Assert.Empty(events);
    }

    [Fact]
    public async Task AMessageNobodyAskedForIsHandedToTheEventHandler()
    {
        await using var server = new FakeCtraderServer { Handler = _ => SymbolsAnswer("EURUSD") };
        await using var connection = server.Connection();
        var events = new List<ProtoMessage>();
        connection.EventReceived += events.Add;
        await AskForSymbolsAsync(connection);

        await server.PushAsync(FakeCtraderServer.Answer(ProtoOAPayloadType.ProtoOaSpotEvent, new ProtoOASpotEvent { CtidTraderAccountId = FakeCtraderServer.AccountId, SymbolId = 1 }));

        var seen = await server.WaitForAsync(() => events.FirstOrDefault(), "the event");
        Assert.Equal((uint)ProtoOAPayloadType.ProtoOaSpotEvent, seen.PayloadType);
    }

    [Fact]
    public async Task ItSendsAHeartbeatWhileItIsQuietAndTheServersHeartbeatsAreNotAnEvent()
    {
        await using var server = new FakeCtraderServer { Handler = _ => SymbolsAnswer("EURUSD") };
        await using var connection = server.Connection(s => s.HeartbeatInterval = TimeSpan.FromMilliseconds(50));
        var events = new List<ProtoMessage>();
        connection.EventReceived += events.Add;
        await AskForSymbolsAsync(connection);

        await server.WaitForAsync(() => server.Received.Count(m => m.PayloadType == (uint)ProtoPayloadType.HeartbeatEvent) >= 2 ? "yes" : null, "two heartbeats");
        await server.PushAsync(new ProtoMessage { PayloadType = (uint)ProtoPayloadType.HeartbeatEvent, Payload = new ProtoHeartbeatEvent().ToByteString() });
        await AskForSymbolsAsync(connection);

        Assert.Empty(events);
    }

    [Fact]
    public async Task ARequestsOfAnUnconfiguredConnectionFailsBeforeAnythingIsSent()
    {
        await using var server = new FakeCtraderServer();
        await using var connection = server.Connection(s => s.AccessToken = null);

        var failure = await Assert.ThrowsAsync<CtraderException>(() => AskForSymbolsAsync(connection));

        Assert.Contains("not configured", failure.Message);
        Assert.Equal(0, server.Connections);
    }

    [Fact]
    public async Task ErrorTextsNeverHoldTheSecretsOrTheAddress()
    {
        // Nothing is listening on this port: the failure is the connect itself.
        var settings = new CtraderExchangeConfigurations
        {
            ClientId = FakeCtraderServer.ClientId, ClientSecret = FakeCtraderServer.ClientSecret, AccessToken = FakeCtraderServer.AccessToken,
            AccountId = FakeCtraderServer.AccountId, Host = "127.0.0.1", Port = 1, UseTls = false, RequestTimeout = TimeSpan.FromSeconds(2),
        };
        await using var connection = new CtraderConnection(Options.Create(settings), NullLogger<CtraderConnection>.Instance);

        var failure = await Assert.ThrowsAsync<CtraderException>(() => AskForSymbolsAsync(connection));

        Assert.DoesNotContain(FakeCtraderServer.ClientSecret, failure.Message);
        Assert.DoesNotContain(FakeCtraderServer.AccessToken, failure.Message);
        Assert.DoesNotContain("127.0.0.1", failure.Message);
    }

    [Fact]
    public async Task HistoricalRequestsAreHeldBackToTheirOwnLimit()
    {
        await using var server = new FakeCtraderServer { Handler = _ => SymbolsAnswer("EURUSD") };
        await using var connection = server.Connection(s => s.HistoricalRequestsPerSecond = 2);
        await AskForSymbolsAsync(connection);   // sign in first, so only the limiter is timed

        var started = DateTime.UtcNow;
        for (var i = 0; i < 4; i++)
            await connection.RequestAsync<ProtoOASymbolsListRes>(ProtoOAPayloadType.ProtoOaSymbolsListReq,
                new ProtoOASymbolsListReq { CtidTraderAccountId = FakeCtraderServer.AccountId }, historical: true, Ct);

        // Four requests at two a second cannot finish in less than about a second (the first two go at once, the rest wait for the window).
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(800), "the limit was not applied");
    }
}
