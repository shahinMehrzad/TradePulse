using System.Collections.Concurrent;
using System.Threading.Channels;
using Application.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Notifications;

/// <summary>
/// Where the security alerts go. Its own bot and chat (<c>SecurityAlerts:Telegram:*</c>), not the collector's: the collector's token stays out of
/// the API's environment, and a message in this chat always means somebody is trying to get into an account. Without it set, nothing is sent
/// (the audit log still has the entry).
/// </summary>
public sealed class SecurityAlertsOptions
{
    public const string SectionName = "SecurityAlerts";

    public string InstanceName { get; set; } = "";
    public TelegramOptions Telegram { get; set; } = new();
}

/// <summary>
/// <see cref="ISecurityAlerts"/>: puts the alert on a small queue and returns, so a sign-in never waits for Telegram. At most one alert of the
/// same key goes out every ten minutes (a guess that goes on for an hour must not fill the chat), and a full queue drops the new alert.
/// </summary>
public sealed class SecurityAlertQueue(TimeProvider time) : ISecurityAlerts
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

/// <summary>Sends what <see cref="SecurityAlertQueue"/> holds, one at a time. A message that cannot be sent is logged without its address (the bot token is in it) and dropped.</summary>
public sealed class SecurityAlertDispatcher(
    SecurityAlertQueue queue, IHttpClientFactory httpClients, IOptions<SecurityAlertsOptions> options, ILogger<SecurityAlertDispatcher> logger) : BackgroundService
{
    public const string HttpClientName = "security-alerts-telegram";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        TelegramNotifier? telegram = null;
        if (settings.Telegram.IsConfigured)
        {
            var label = string.IsNullOrWhiteSpace(settings.InstanceName) ? "tradepulse" : settings.InstanceName.Trim();
            telegram = new TelegramNotifier(httpClients.CreateClient(HttpClientName),
                Options.Create(new AlertsOptions { InstanceName = $"{label} security", Telegram = settings.Telegram }));
        }
        else
        {
            logger.LogInformation("No Telegram bot is set for security alerts (SecurityAlerts:Telegram): they are only written to the audit log.");
        }

        await foreach (var alert in queue.Reader.ReadAllAsync(stoppingToken))
        {
            if (telegram is null)
                continue;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await telegram.SendAsync(new Alert($"\U0001F512 {alert.Title}", alert.Body), timeout.Token);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // The exception can carry the address of the request, which holds the bot token: only what the notifier chose to say.
                logger.LogWarning("A security alert could not be sent: {Reason}", ex is NotificationException ? ex.Message : ex.GetType().Name);
            }
        }
    }
}
