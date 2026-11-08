using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Application.Notifications;
using Application.Signals;
using Microsoft.Extensions.Options;

namespace Infrastructure.Notifications;

/// <summary>The bot and the chat the paper trades are told in: a bot of its own, so these messages never mix with the collector's alerts.</summary>
public sealed class TradeChatOptions
{
    public const string SectionName = "Signals:Telegram";

    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
}

/// <summary>
/// The chat of the paper trades over Telegram's bot API: send a message, send a reply under it, edit it. Every failure is a
/// <see cref="NotificationException"/> whose words never hold the bot token (it is part of the address, so nothing from an exception that
/// may carry the address is passed on). A request that takes too long is such a failure too, not a cancellation: only the caller's own token
/// cancels.
/// </summary>
public sealed class TelegramTradeChat(HttpClient http, IOptions<TradeChatOptions> options) : ITradeChat
{
    private const string Html = "HTML";

    public async Task<ChatMessage> SendAsync(string html, CancellationToken cancellationToken) =>
        await PostMessageAsync(new { chat_id = options.Value.ChatId, text = html, parse_mode = Html, disable_web_page_preview = true }, cancellationToken);

    public async Task<ChatMessage> ReplyAsync(ChatMessage to, string html, CancellationToken cancellationToken) =>
        await PostMessageAsync(new
        {
            chat_id = to.ChatId,
            text = html,
            parse_mode = Html,
            disable_web_page_preview = true,
            reply_parameters = new { message_id = to.MessageId, allow_sending_without_reply = true },
        }, cancellationToken);

    public async Task<bool> EditAsync(ChatMessage message, string html, CancellationToken cancellationToken)
    {
        var answer = await PostAsync("editMessageText",
            new { chat_id = message.ChatId, message_id = message.MessageId, text = html, parse_mode = Html, disable_web_page_preview = true }, cancellationToken);

        if (answer.Status == HttpStatusCode.OK)
            return true;

        // Saying the same words again is no failure, and a message that is gone (deleted, too old) is told apart from a chat that is down.
        if (answer.Status == HttpStatusCode.BadRequest && answer.Reason is { } reason)
        {
            if (reason.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
                return true;

            if (reason.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("message can't be edited", StringComparison.OrdinalIgnoreCase)
                || reason.Contains("MESSAGE_ID_INVALID", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        throw answer.ToException();
    }

    private async Task<ChatMessage> PostMessageAsync(object payload, CancellationToken cancellationToken)
    {
        var answer = await PostAsync("sendMessage", payload, cancellationToken);
        if (answer.Status != HttpStatusCode.OK)
            throw answer.ToException();

        return answer.Message ?? throw new NotificationException("Telegram accepted the message but did not say where it is");
    }

    /// <summary>What Telegram answered: the status, the reason of a refusal, how long to wait if it asked, and where a sent message is.</summary>
    private sealed record Answer(HttpStatusCode Status, string? Reason, TimeSpan? RetryAfter, ChatMessage? Message)
    {
        public NotificationException ToException() =>
            new($"Telegram answered {(int)Status}{(Reason is null ? "" : $": {Reason}")}", RetryAfter);
    }

    private async Task<Answer> PostAsync(string method, object payload, CancellationToken cancellationToken)
    {
        var token = options.Value.BotToken;

        HttpResponseMessage response;
        try
        {
            // "./": a token looks like "123456:ABC", and without it "bot123456:" would be read as the scheme of an address.
            response = await http.PostAsJsonAsync($"./bot{token}/{method}", payload, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // The bot token is in the address: nothing from the exception is passed on.
            throw new NotificationException($"Telegram could not be reached ({ex.HttpRequestError})");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NotificationException("Telegram did not answer in time");
        }

        using (response)
        {
            string? body;
            try
            {
                body = await response.Content.ReadAsStringAsync(cancellationToken);
            }
            catch (HttpRequestException)
            {
                body = null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                body = null;
            }

            return response.IsSuccessStatusCode
                ? new Answer(response.StatusCode, null, null, MessageOf(body))
                : new Answer(response.StatusCode, ReasonOf(body, token), RetryAfterOf(body), null);
        }
    }

    /// <summary>The chat and the id of the message Telegram says it made (<c>result.chat.id</c>, <c>result.message_id</c>); null when the answer has no such thing (an edit's).</summary>
    private static ChatMessage? MessageOf(string? body)
    {
        if (body is null)
            return null;

        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("message_id", out var id) && id.TryGetInt32(out var messageId)
                && result.TryGetProperty("chat", out var chat) && chat.TryGetProperty("id", out var chatId) && chatId.TryGetInt64(out var chatNumber))
                return new ChatMessage(chatNumber, messageId);
        }
        catch (JsonException)
        {
            // Not an answer of the shape: it has no message in it.
        }

        return null;
    }

    /// <summary>Telegram's <c>parameters.retry_after</c> (seconds) of a "Too Many Requests" answer, or null.</summary>
    private static TimeSpan? RetryAfterOf(string? body)
    {
        if (body is null)
            return null;

        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("parameters", out var parameters)
                && parameters.TryGetProperty("retry_after", out var seconds)
                && seconds.TryGetInt32(out var value) && value > 0
                    ? TimeSpan.FromSeconds(value)
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>What Telegram says went wrong, a short sentence about the request that never holds the token (if it ever did, it is blanked). Null when there is nothing usable.</summary>
    private static string? ReasonOf(string? body, string token)
    {
        if (body is null)
            return null;

        try
        {
            using var json = JsonDocument.Parse(body);
            if (!json.RootElement.TryGetProperty("description", out var description) || description.GetString() is not { Length: > 0 } text)
                return null;

            text = text.Replace(token, "***", StringComparison.Ordinal);
            return text.Length <= 200 ? text : text[..199] + "…";
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
