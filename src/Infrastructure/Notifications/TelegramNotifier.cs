using System.Net.Http.Json;
using Application.Notifications;
using Microsoft.Extensions.Options;

namespace Infrastructure.Notifications;

public sealed class TelegramNotifier(HttpClient http, IOptions<AlertsOptions> options) : INotifier
{
    public const int MaxLength = 4000;

    public string Name => "Telegram";

    public async Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        // A message with formatting (bold, emoji) goes as HTML. If Telegram cannot read it, the same words go once more as plain text: a
        // formatting mistake must not cost the admin the message.
        if (alert.Html is { Length: > 0 and <= MaxLength } html)
        {
            var failure = await PostAsync(new { chat_id = options.Value.Telegram.ChatId, text = html, parse_mode = "HTML", disable_web_page_preview = true }, cancellationToken);
            if (failure is null)
                return;

            if (failure.StatusCode != System.Net.HttpStatusCode.BadRequest)
                throw failure.ToException();
        }

        var settings = options.Value;
        var plain = await PostAsync(new { chat_id = settings.Telegram.ChatId, text = Compose(settings.Label, alert), disable_web_page_preview = true }, cancellationToken);
        if (plain is not null)
            throw plain.ToException();
    }

    /// <summary>What went wrong with a request to Telegram, or null when it was accepted.</summary>
    private sealed record Failure(System.Net.HttpStatusCode StatusCode, string? Reason, TimeSpan? RetryAfter = null)
    {
        public NotificationException ToException() => new($"Telegram answered {(int)StatusCode}{(Reason is null ? "" : $": {Reason}")}", RetryAfter);
    }

    private async Task<Failure?> PostAsync(object payload, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        HttpResponseMessage response;
        try
        {
            // "./": a token looks like "123456:ABC", and without it "bot123456:" would be read as the scheme of an address.
            response = await http.PostAsJsonAsync($"./bot{settings.Telegram.BotToken}/sendMessage", payload, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // The bot token is in the address: nothing from the exception is passed on.
            throw new NotificationException($"Telegram could not be reached ({ex.HttpRequestError})");
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
                return null;

            // The body is read once: the reason and, for a 429, how long Telegram asks us to wait.
            var body = await ReadBodyAsync(response, cancellationToken);
            return new Failure(response.StatusCode, ReasonOf(body, settings.Telegram.BotToken), RetryAfterOf(body));
        }
    }

    private static async Task<string?> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Telegram's <c>parameters.retry_after</c> (seconds) of a "Too Many Requests" answer, or null.</summary>
    private static TimeSpan? RetryAfterOf(string? body)
    {
        if (body is null)
            return null;

        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("parameters", out var parameters)
                && parameters.TryGetProperty("retry_after", out var seconds)
                && seconds.TryGetInt32(out var value) && value > 0
                    ? TimeSpan.FromSeconds(value)
                    : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// What Telegram says went wrong ("Bad Request: chat not found", "...group chat was upgraded to a supergroup chat"), so that a wrong
    /// chat id or token is told apart without guessing. It is a short sentence about the request and never holds the token; should it
    /// ever, the token is blanked. Null when there is nothing usable.
    /// </summary>
    private static string? ReasonOf(string? responseBody, string token)
    {
        if (responseBody is null)
            return null;

        try
        {
            using var body = System.Text.Json.JsonDocument.Parse(responseBody);
            if (!body.RootElement.TryGetProperty("description", out var description) || description.GetString() is not { Length: > 0 } text)
                return null;

            text = text.Replace(token, "***", StringComparison.Ordinal);
            return text.Length <= 200 ? text : text[..199] + "…";
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    public static string Compose(string label, Alert alert)
    {
        var text = $"[{label}] {alert.Title}\n{alert.Body}";
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}
