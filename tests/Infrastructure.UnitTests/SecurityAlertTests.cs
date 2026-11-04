using System.Net;
using Application.Notifications;
using Infrastructure.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Infrastructure.UnitTests;

/// <summary>The alerts about somebody trying to get into an account: at most one of a kind every ten minutes, never blocking, and never leaking the bot token.</summary>
public class SecurityAlertTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Token = "123456:SECRET-SECURITY-TOKEN";

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));

    private static SecurityAlert Alert(string key, string title = "t") => new(key, title, "body");

    private static List<SecurityAlert> Drain(SecurityAlertQueue queue)
    {
        var all = new List<SecurityAlert>();
        while (queue.Reader.TryRead(out var alert))
            all.Add(alert);
        return all;
    }

    // ---- the queue

    [Fact]
    public void TheSameKindOfAlertAboutTheSameAccountGoesOutOnceInTenMinutes()
    {
        var queue = new SecurityAlertQueue(_time);

        queue.Raise(Alert("two-factor-code:alice"));
        queue.Raise(Alert("two-factor-code:alice"));
        _time.Advance(TimeSpan.FromMinutes(9));
        queue.Raise(Alert("two-factor-code:alice"));

        Assert.Single(Drain(queue));
    }

    [Fact]
    public void AfterTheQuietTimeTheNextOneGoesOut()
    {
        var queue = new SecurityAlertQueue(_time);
        queue.Raise(Alert("two-factor-code:alice"));

        _time.Advance(SecurityAlertQueue.Quiet + TimeSpan.FromSeconds(1));
        queue.Raise(Alert("two-factor-code:alice"));

        Assert.Equal(2, Drain(queue).Count);
    }

    [Fact]
    public void AnotherAccountOrAnotherKindIsNotHeldBack()
    {
        var queue = new SecurityAlertQueue(_time);

        queue.Raise(Alert("two-factor-code:alice"));
        queue.Raise(Alert("two-factor-code:bob"));
        queue.Raise(Alert("two-factor-lock:alice"));

        Assert.Equal(3, Drain(queue).Count);
    }

    [Fact]
    public void AFullQueueDropsTheNewAlertInsteadOfBlockingOrThrowing()
    {
        var queue = new SecurityAlertQueue(_time);

        for (var i = 0; i < 200; i++)
            queue.Raise(Alert($"key:{i}"));

        Assert.Equal(50, Drain(queue).Count);
    }

    // ---- sending

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        private readonly List<string> _bodies = [];
        public IReadOnlyList<string> Bodies { get { lock (_bodies) return [.. _bodies]; } }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_bodies) _bodies.Add(body);
            return respond(request);
        }
    }

    private sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.telegram.org/") };
    }

    private sealed class RecordingLogger : ILogger<SecurityAlertDispatcher>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception) + (exception?.ToString() ?? ""));
        }
    }

    private static IOptions<SecurityAlertsOptions> Settings(bool configured = true) => Microsoft.Extensions.Options.Options.Create(new SecurityAlertsOptions
    {
        InstanceName = "box",
        Telegram = configured ? new TelegramOptions { BotToken = Token, ChatId = "42" } : new TelegramOptions()
    });

    private static async Task<T> UntilAsync<T>(Func<T> read, Func<T, bool> done)
    {
        for (var i = 0; i < 100 && !done(read()); i++)
            await Task.Delay(50, Ct);
        return read();
    }

    [Fact]
    public async Task AnAlertGoesToTheSecurityBotWithItsOwnNameAndTheLock()
    {
        var queue = new SecurityAlertQueue(_time);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var dispatcher = new SecurityAlertDispatcher(queue, new StubFactory(handler), Settings(), new RecordingLogger());
        await dispatcher.StartAsync(Ct);

        queue.Raise(new SecurityAlert("k", "Wrong two-factor code for alice", "from 203.0.113.7"));
        var bodies = await UntilAsync(() => handler.Bodies, b => b.Count > 0);
        await dispatcher.StopAsync(Ct);

        var body = Assert.Single(bodies);
        Assert.Contains("box security", body);          // the label says it is the security bot, not the collector's
        Assert.Contains("Wrong two-factor code for alice", body);
        Assert.Contains("203.0.113.7", body);
        Assert.Contains("\"chat_id\":\"42\"", body);
        Assert.DoesNotContain(Token, body);             // the token is in the address, never in the message
    }

    [Fact]
    public async Task WithoutABotNothingIsSentAndNothingBreaks()
    {
        var queue = new SecurityAlertQueue(_time);
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var dispatcher = new SecurityAlertDispatcher(queue, new StubFactory(handler), Settings(configured: false), new RecordingLogger());
        await dispatcher.StartAsync(Ct);

        queue.Raise(Alert("k"));
        await Task.Delay(300, Ct);
        await dispatcher.StopAsync(Ct);

        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task ABotThatRefusesIsLoggedWithoutTheTokenAndTheNextAlertStillGoes()
    {
        var queue = new SecurityAlertQueue(_time);
        var calls = 0;
        var handler = new StubHandler(_ => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent($"{{\"description\":\"Unauthorized {Token}\"}}") }
            : new HttpResponseMessage(HttpStatusCode.OK));
        var logger = new RecordingLogger();
        using var dispatcher = new SecurityAlertDispatcher(queue, new StubFactory(handler), Settings(), logger);
        await dispatcher.StartAsync(Ct);

        queue.Raise(Alert("first"));
        queue.Raise(Alert("second"));
        var bodies = await UntilAsync(() => handler.Bodies, b => b.Count >= 2);
        await dispatcher.StopAsync(Ct);

        Assert.Equal(2, bodies.Count);
        Assert.Contains(logger.Lines, line => line.Contains("could not be sent"));
        Assert.DoesNotContain(logger.Lines, line => line.Contains(Token));
    }
}
