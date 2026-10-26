using Application.Notifications;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.Alerts;

/// <summary>
/// Sends the raised alerts to every channel, away from everything else: the collection only drops an alert in a queue. A channel
/// that fails is logged and the others still get the message; nothing here can stop the host. A burst is capped per hour.
/// </summary>
public sealed class AlertDispatcher(
    AlertQueue queue,
    IEnumerable<INotifier> notifiers,
    IOptions<AlertSettings> options,
    TimeProvider timeProvider,
    ILogger<AlertDispatcher> logger) : BackgroundService
{
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(20);

    private readonly Queue<DateTimeOffset> _sent = new();
    private int _suppressed;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channels = notifiers.ToList();
        if (channels.Count == 0)
            logger.LogInformation("No alert channel is configured; alerts are off.");
        else
            logger.LogInformation("Alerts go to: {Channels}.", string.Join(", ", channels.Select(c => c.Name)));

        try
        {
            await foreach (var alert in queue.Reader.ReadAllAsync(stoppingToken))
            {
                if (channels.Count > 0)
                    await DispatchAsync(alert, channels, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task DispatchAsync(Alert alert, List<INotifier> channels, CancellationToken stoppingToken)
    {
        var now = timeProvider.GetUtcNow();
        while (_sent.Count > 0 && now - _sent.Peek() >= TimeSpan.FromHours(1))
            _sent.Dequeue();

        var max = Math.Max(1, options.Value.MaxPerHour);
        if (_sent.Count >= max)
        {
            _suppressed++;
            logger.LogWarning("Alert suppressed (the limit of {Max} an hour is reached): {Title}", max, alert.Title);
            return;
        }

        if (_suppressed > 0)
        {
            var held = new Alert("Alerts were held back", $"{_suppressed} alert(s) were not sent because the limit of {max} an hour was reached.");
            _suppressed = 0;
            _sent.Enqueue(now);
            await SendToAllAsync(held, channels, stoppingToken);
        }

        _sent.Enqueue(now);
        await SendToAllAsync(alert, channels, stoppingToken);
    }

    private async Task SendToAllAsync(Alert alert, List<INotifier> channels, CancellationToken stoppingToken) =>
        await Task.WhenAll(channels.Select(channel => SendAsync(channel, alert, stoppingToken)));

    private async Task SendAsync(INotifier channel, Alert alert, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(SendTimeout);

        try
        {
            await channel.SendAsync(alert, timeout.Token);
        }
        catch (NotificationException ex)
        {
            logger.LogWarning("Alert channel {Channel} failed: {Reason}", channel.Name, ex.Message);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Only the type: the message of an unexpected failure can carry an address with a secret in it.
            logger.LogWarning("Alert channel {Channel} failed ({Error}).", channel.Name, ex.GetType().Name);
        }
    }
}
