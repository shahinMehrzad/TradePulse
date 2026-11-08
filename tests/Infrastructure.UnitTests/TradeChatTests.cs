using System.Net;
using System.Text;
using System.Text.Json;
using Application.Notifications;
using Application.Signals;
using Infrastructure.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Infrastructure.UnitTests;

/// <summary>The chat of the paper trades over Telegram: what is posted, what is understood of the answers, and that no failure carries the token.</summary>
public class TradeChatTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Token = "123456:SECRET-TOKEN";
    private const string Chat = "-1001234567890";

    /// <summary>Answers each request in turn (the last one for every later request) and keeps what was asked.</summary>
    private sealed class Handler(params Func<HttpResponseMessage>[] answers) : HttpMessageHandler
    {
        private int _next;

        public List<(string Path, JsonElement Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, JsonDocument.Parse(body).RootElement.Clone()));
            return answers[Math.Min(_next++, answers.Length - 1)]();
        }
    }

    private static TelegramTradeChat NewChat(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://api.telegram.org/") },
        Options.Create(new TradeChatOptions { BotToken = Token, ChatId = Chat }));

    private static Func<HttpResponseMessage> Ok(string json) => () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Func<HttpResponseMessage> Refusal(HttpStatusCode status, string json) =>
        () => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static Func<HttpResponseMessage> Sent(int messageId = 77, long chat = -1001234567890) =>
        Ok($$"""{"ok":true,"result":{"message_id":{{messageId}},"chat":{"id":{{chat}},"type":"supergroup"},"date":1} }""");

    // ---- send ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMessageIsPostedAsHtmlToTheChatOfTheBotAndItsPlaceIsReturned()
    {
        var handler = new Handler(Sent());

        var message = await NewChat(handler).SendAsync("<b>hello</b>", Ct);

        Assert.Equal(new ChatMessage(-1001234567890, 77), message);
        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/bot{Token}/sendMessage", request.Path);
        Assert.Equal(Chat, request.Body.GetProperty("chat_id").GetString());
        Assert.Equal("<b>hello</b>", request.Body.GetProperty("text").GetString());
        Assert.Equal("HTML", request.Body.GetProperty("parse_mode").GetString());
        Assert.True(request.Body.GetProperty("disable_web_page_preview").GetBoolean());
    }

    [Fact]
    public async Task TheChatThatTheMessageIsInComesFromTheAnswerNotFromTheSettings()
    {
        // The settings may name a channel by its @name; the number that follows is what an edit needs.
        var handler = new Handler(Sent(messageId: 5, chat: -100999));

        var message = await NewChat(handler).SendAsync("x", Ct);

        Assert.Equal(new ChatMessage(-100999, 5), message);
    }

    [Fact]
    public async Task AnAnswerThatDoesNotSayWhereTheMessageIsIsAFailure()
    {
        var handler = new Handler(Ok("""{"ok":true,"result":true}"""));

        await Assert.ThrowsAsync<NotificationException>(() => NewChat(handler).SendAsync("x", Ct));
    }

    // ---- reply --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AReplyIsPostedUnderTheMessageInItsOwnChat()
    {
        var handler = new Handler(Sent(messageId: 78));

        var reply = await NewChat(handler).ReplyAsync(new ChatMessage(-1001234567890, 77), "answer", Ct);

        Assert.Equal(78, reply.MessageId);
        var body = Assert.Single(handler.Requests).Body;
        Assert.Equal(-1001234567890, body.GetProperty("chat_id").GetInt64());
        Assert.Equal(77, body.GetProperty("reply_parameters").GetProperty("message_id").GetInt32());
        Assert.True(body.GetProperty("reply_parameters").GetProperty("allow_sending_without_reply").GetBoolean());
    }

    // ---- edit ---------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AnEditNamesTheMessageAndItsNewWords()
    {
        var handler = new Handler(Ok("""{"ok":true,"result":{"message_id":77,"chat":{"id":-1001234567890}}}"""));

        Assert.True(await NewChat(handler).EditAsync(new ChatMessage(-1001234567890, 77), "<b>new</b>", Ct));

        var request = Assert.Single(handler.Requests);
        Assert.Equal($"/bot{Token}/editMessageText", request.Path);
        Assert.Equal(-1001234567890, request.Body.GetProperty("chat_id").GetInt64());
        Assert.Equal(77, request.Body.GetProperty("message_id").GetInt32());
        Assert.Equal("<b>new</b>", request.Body.GetProperty("text").GetString());
        Assert.Equal("HTML", request.Body.GetProperty("parse_mode").GetString());
    }

    [Fact]
    public async Task SayingTheSameWordsAgainIsASuccess()
    {
        var handler = new Handler(Refusal(HttpStatusCode.BadRequest,
            """{"ok":false,"error_code":400,"description":"Bad Request: message is not modified: specified new message content and reply markup are exactly the same"}"""));

        Assert.True(await NewChat(handler).EditAsync(new ChatMessage(1, 2), "same", Ct));
    }

    [Theory]
    [InlineData("Bad Request: message to edit not found")]
    [InlineData("Bad Request: message can't be edited")]
    [InlineData("Bad Request: MESSAGE_ID_INVALID")]
    public async Task AMessageThatIsGoneIsToldApartFromAFailure(string description)
    {
        var handler = new Handler(Refusal(HttpStatusCode.BadRequest, $$"""{"ok":false,"error_code":400,"description":"{{description}}"}"""));

        Assert.False(await NewChat(handler).EditAsync(new ChatMessage(1, 2), "x", Ct));
    }

    [Fact]
    public async Task AnyOtherRefusalOfAnEditIsAFailureWithTheReason()
    {
        var handler = new Handler(Refusal(HttpStatusCode.BadRequest, """{"ok":false,"error_code":400,"description":"Bad Request: can't parse entities"}"""));

        var error = await Assert.ThrowsAsync<NotificationException>(() => NewChat(handler).EditAsync(new ChatMessage(1, 2), "<b>x", Ct));

        Assert.Equal("Telegram answered 400: Bad Request: can't parse entities", error.Message);
    }

    // ---- failures -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TooManyRequestsCarriesHowLongToWait()
    {
        var handler = new Handler(Refusal(HttpStatusCode.TooManyRequests,
            """{"ok":false,"error_code":429,"description":"Too Many Requests: retry after 17","parameters":{"retry_after":17}}"""));

        var error = await Assert.ThrowsAsync<NotificationException>(() => NewChat(handler).SendAsync("x", Ct));

        Assert.Equal(TimeSpan.FromSeconds(17), error.RetryAfter);
        Assert.Contains("429", error.Message);
    }

    [Fact]
    public async Task ATokenThatTelegramRepeatsIsBlanked()
    {
        var handler = new Handler(Refusal(HttpStatusCode.Unauthorized, $$"""{"ok":false,"error_code":401,"description":"Unauthorized {{Token}}"}"""));

        var error = await Assert.ThrowsAsync<NotificationException>(() => NewChat(handler).SendAsync("x", Ct));

        Assert.DoesNotContain(Token, error.Message);
        Assert.Contains("***", error.Message);
    }

    [Fact]
    public async Task ATelegramThatCannotBeReachedIsAFailureWithoutTheAddress()
    {
        var error = await Assert.ThrowsAsync<NotificationException>(() => NewChat(new ThrowingHandler(new HttpRequestException($"cannot reach https://api.telegram.org/bot{Token}/sendMessage"))).SendAsync("x", Ct));

        Assert.DoesNotContain(Token, error.Message);
        Assert.StartsWith("Telegram could not be reached", error.Message);
    }

    [Fact]
    public async Task ARequestThatTakesTooLongIsAFailureNotACancellation()
    {
        var chat = NewChat(new ThrowingHandler(new TaskCanceledException("the request timed out")));

        var error = await Assert.ThrowsAsync<NotificationException>(() => chat.SendAsync("x", Ct));

        Assert.Equal("Telegram did not answer in time", error.Message);
    }

    [Fact]
    public async Task TheCallersOwnCancellationStillCancels()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var chat = NewChat(new ThrowingHandler(new TaskCanceledException("cancelled", null, cancelled.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.SendAsync("x", cancelled.Token));
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw exception;
    }

    // ---- the settings -------------------------------------------------------------------------------------------------------------

    private static ServiceProvider Provider(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTradeChat(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void WithABotAndAChatTheMessagesGoToTelegram()
    {
        using var provider = Provider(new() { ["Signals:Telegram:BotToken"] = Token, ["Signals:Telegram:ChatId"] = Chat });

        Assert.IsType<TelegramTradeChat>(provider.GetRequiredService<ITradeChat>());
    }

    [Fact]
    public void WithoutThemTheMessagesOnlyGoToTheLog()
    {
        using var provider = Provider([]);

        Assert.IsType<LogOnlyTradeChat>(provider.GetRequiredService<ITradeChat>());
    }

    [Fact]
    public void ABotWithoutAChatIsNotEnough()
    {
        using var provider = Provider(new() { ["Signals:Telegram:BotToken"] = Token });

        Assert.IsType<LogOnlyTradeChat>(provider.GetRequiredService<ITradeChat>());
    }

    [Fact]
    public async Task TheLogOnlyChatHandsOutIdsAndNeverRefuses()
    {
        using var provider = Provider([]);
        var chat = provider.GetRequiredService<ITradeChat>();

        var first = await chat.SendAsync("a", Ct);
        var second = await chat.SendAsync("b", Ct);

        Assert.NotEqual(first.MessageId, second.MessageId);
        Assert.True(await chat.EditAsync(first, "c", Ct));
        Assert.NotEqual(0, (await chat.ReplyAsync(first, "d", Ct)).MessageId);
    }
}
