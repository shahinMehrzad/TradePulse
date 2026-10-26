using System.Net;
using System.Text.Json;
using Application.Notifications;
using Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.UnitTests;

public class NotifierTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Token = "123456:SECRET-TOKEN";

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static IOptions<AlertsOptions> Options(string instance = "box") => Microsoft.Extensions.Options.Options.Create(new AlertsOptions
    {
        InstanceName = instance,
        Telegram = new TelegramOptions { BotToken = Token, ChatId = "42" },
    });

    private static TelegramNotifier Telegram(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") }, Options());

    // ---- Telegram ----

    [Fact]
    public async Task TheMessageIsPostedToTheBotWithTheChatAndTheInstanceInFront()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));

        await Telegram(handler).SendAsync(new Alert("Collector problem", "🔴 database: unhealthy"), Ct);

        Assert.Equal($"/bot{Token}/sendMessage", handler.Path);
        using var body = JsonDocument.Parse(handler.Body!);
        Assert.Equal("42", body.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("[box] Collector problem\n🔴 database: unhealthy", body.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ARefusalIsReportedWithTheStatusAndWithoutTheAddressThatHoldsTheToken()
    {
        var notifier = Telegram(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));

        var error = await Assert.ThrowsAsync<NotificationException>(() => notifier.SendAsync(new Alert("t", "b"), Ct));

        Assert.Equal("Telegram answered 401", error.Message);
        Assert.DoesNotContain("SECRET", error.Message);
    }

    [Fact]
    public async Task AConnectionFailureIsReportedWithoutWhatTheExceptionSays()
    {
        var notifier = Telegram(new StubHandler(_ => throw new HttpRequestException($"Failed to reach https://api.telegram.org/bot{Token}/sendMessage")));

        var error = await Assert.ThrowsAsync<NotificationException>(() => notifier.SendAsync(new Alert("t", "b"), Ct));

        Assert.DoesNotContain("SECRET", error.Message);
        Assert.StartsWith("Telegram could not be reached", error.Message);
    }

    [Fact]
    public void ALongMessageIsCutBelowTheLimitOfTelegram()
    {
        var text = TelegramNotifier.Compose("box", new Alert("t", new string('x', 10_000)));

        Assert.Equal(TelegramNotifier.MaxLength, text.Length);
        Assert.EndsWith("…", text);
    }

    // ---- Email ----

    private sealed class RecordingTransport(Exception? failure = null) : IEmailTransport
    {
        public string? Subject { get; private set; }
        public string? Body { get; private set; }

        public Task SendAsync(string subject, string body, CancellationToken cancellationToken)
        {
            if (failure is not null)
                throw failure;

            (Subject, Body) = (subject, body);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TheMailNamesTheInstanceInTheSubjectAndAtTheEndOfTheBody()
    {
        var transport = new RecordingTransport();

        await new EmailNotifier(transport, Options("prod-1")).SendAsync(new Alert("Collector warning", "⚠️ candle-freshness: degraded"), Ct);

        Assert.Equal("[TradePulse] prod-1: Collector warning", transport.Subject);
        Assert.Equal("⚠️ candle-freshness: degraded\n\n-- prod-1", transport.Body);
    }

    [Fact]
    public async Task AFailureOfTheMailServerComesOutAsANotificationFailure()
    {
        var notifier = new EmailNotifier(new RecordingTransport(new NotificationException("The mail server did not accept the message (MailboxBusy)")), Options());

        await Assert.ThrowsAsync<NotificationException>(() => notifier.SendAsync(new Alert("t", "b"), Ct));
    }

    // ---- which channels exist ----

    private static List<string> ChannelsFor(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddLogging().AddNotifiers(configuration);
        using var provider = services.BuildServiceProvider();
        return [.. provider.GetServices<INotifier>().Select(n => n.Name).Order()];
    }

    [Fact]
    public void WithoutSettingsThereIsNoChannel() => Assert.Empty(ChannelsFor([]));

    [Fact]
    public void AChannelExistsOnlyWhenItsSettingsAreComplete()
    {
        Assert.Empty(ChannelsFor(new() { ["Alerts:Telegram:BotToken"] = Token }));                                   // no chat
        Assert.Empty(ChannelsFor(new() { ["Alerts:Email:Host"] = "smtp.example.com", ["Alerts:Email:From"] = "a@b.c" })); // no recipient

        Assert.Equal(["Telegram"], ChannelsFor(new() { ["Alerts:Telegram:BotToken"] = Token, ["Alerts:Telegram:ChatId"] = "42" }));
        Assert.Equal(["Email", "Telegram"], ChannelsFor(new()
        {
            ["Alerts:Telegram:BotToken"] = Token,
            ["Alerts:Telegram:ChatId"] = "42",
            ["Alerts:Email:Host"] = "smtp.example.com",
            ["Alerts:Email:From"] = "tradepulse@example.com",
            ["Alerts:Email:To"] = "me@example.com; you@example.com",
        }));
    }
}
