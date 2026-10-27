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
        var settings = options.Value;

        HttpResponseMessage response;
        try
        {
            // "./": a token looks like "123456:ABC", and without it "bot123456:" would be read as the scheme of an address.
            response = await http.PostAsJsonAsync($"./bot{settings.Telegram.BotToken}/sendMessage",
                new { chat_id = settings.Telegram.ChatId, text = Compose(settings.Label, alert), disable_web_page_preview = true }, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            // The bot token is in the address: nothing from the exception is passed on.
            throw new NotificationException($"Telegram could not be reached ({ex.HttpRequestError})");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new NotificationException($"Telegram answered {(int)response.StatusCode}");
        }
    }

    public static string Compose(string label, Alert alert)
    {
        var text = $"[{label}] {alert.Title}\n{alert.Body}";
        return text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}
