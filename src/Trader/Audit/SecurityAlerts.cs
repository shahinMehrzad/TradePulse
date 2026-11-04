using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Trader.Audit;

public sealed class TelegramSettings
{
    public string BotToken { get; set; } = "";
    public string ChatId { get; set; } = "";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(BotToken) && !string.IsNullOrWhiteSpace(ChatId);
}

/// <summary>The security bot, the same one the API tells about wrong sign-in codes: a message in that chat always means somebody is trying to get in.</summary>
public sealed class SecurityAlertsOptions
{
    public const string SectionName = "SecurityAlerts";

    public string InstanceName { get; set; } = "";
    public TelegramSettings Telegram { get; set; } = new();
}

/// <param name="Key">What it is about (kind and user): at most one with the same key every ten minutes.</param>
public sealed record SecurityAlert(string Key, string Title, string Body);

public interface ISecurityAlerts
{
    void Raise(SecurityAlert alert);
}

/// <summary>
/// Puts an alert on a small queue and returns: a request never waits for Telegram and never fails because of it. Never more than one alert
/// of a kind for a user every ten minutes, and a full queue drops the new one. Nothing secret goes in an alert: who, from where, what.
/// </summary>
public sealed class SecurityAlerts(TimeProvider time) : ISecurityAlerts
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);

    private readonly Channel<SecurityAlert> _channel = Channel.CreateBounded<SecurityAlert>(
        new BoundedChannelOptions(50) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastSent = new();

    public ChannelReader<SecurityAlert> Reader => _channel.Reader;

    public void Raise(SecurityAlert alert)
    {
        var now = time.GetUtcNow();
        if (_lastSent.TryGetValue(alert.Key, out var last) && now - last < Quiet)
            return;

        if (_lastSent.Count > 1000)
            foreach (var (key, at) in _lastSent)
                if (now - at >= Quiet)
                    _lastSent.TryRemove(key, out _);

        if (_channel.Writer.TryWrite(alert))
            _lastSent[alert.Key] = now;
    }
}

/// <summary>Sends the queued alerts to the bot, one at a time. A failure is logged without the address (the bot token is in it) and the alert is dropped.</summary>
public sealed class SecurityAlertSender(SecurityAlerts queue, IHttpClientFactory httpClients, IOptions<SecurityAlertsOptions> options, ILogger<SecurityAlertSender> logger)
    : BackgroundService
{
    public const string HttpClientName = "security-alerts-telegram";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Telegram.IsConfigured)
            logger.LogInformation("No Telegram bot is set for security alerts (SecurityAlerts:Telegram): they are only written to the audit log.");

        await foreach (var alert in queue.Reader.ReadAllAsync(stoppingToken))
            await SendNowAsync(alert, stoppingToken);
    }

    /// <summary>Also used by the server's own command line, where nothing runs the queue.</summary>
    public async Task SendNowAsync(SecurityAlert alert, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (!settings.Telegram.IsConfigured)
            return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var label = string.IsNullOrWhiteSpace(settings.InstanceName) ? "tradepulse" : settings.InstanceName.Trim();
            var text = $"[{label} security] \U0001F512 {alert.Title}\n{alert.Body}";

            using var response = await httpClients.CreateClient(HttpClientName).PostAsJsonAsync(
                $"./bot{settings.Telegram.BotToken}/sendMessage",
                new { chat_id = settings.Telegram.ChatId, text = text.Length <= 4000 ? text : text[..3999] + "…", disable_web_page_preview = true },
                timeout.Token);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("A security alert was refused by Telegram (HTTP {Status}).", (int)response.StatusCode);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // The exception can carry the address of the request, which holds the bot token: only its kind is logged.
            logger.LogWarning("A security alert could not be sent ({Reason}).", ex.GetType().Name);
        }
    }
}
