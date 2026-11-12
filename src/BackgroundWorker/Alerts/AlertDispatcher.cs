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

    private const int TitlesInNotice = 5;

    private readonly Queue<DateTimeOffset> _sent = new();

    // What the limit held back, until it is said: how many, between when, and which (the first few titles, once each).
    private int _suppressed;
    private DateTimeOffset _firstSuppressed;
    private DateTimeOffset _lastSuppressed;
    private readonly List<string> _suppressedTitles = [];
    private bool _moreTitles;   // a title did not fit in the list

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channels = notifiers.ToList();
        if (channels.Count == 0)
            logger.LogInformation("No alert channel is configured; alerts are off.");
        else
            logger.LogInformation("Alerts go to: {Channels}.", string.Join(", ", channels.Select(c => c.Name)));

        try
        {
            // The note about held-back alerts goes out as soon as the limit has room again, not only with the next alert: that one may
            // come hours later, and what was held back is often the message that says a problem is over.
            Task<bool>? waiting = null;
            while (true)
            {
                waiting ??= queue.Reader.WaitToReadAsync(stoppingToken).AsTask();

                if (_suppressed > 0 && channels.Count > 0)
                {
                    using var wake = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var room = Task.Delay(TimeUntilRoom(), timeProvider, wake.Token);
                    var first = await Task.WhenAny(waiting, room);
                    await wake.CancelAsync();

                    if (first == room)
                    {
                        await SayHeldBackAsync(channels, stoppingToken);
                        continue;
                    }
                }

                if (!await waiting)
                    break;

                waiting = null;
                while (queue.Reader.TryRead(out var alert))
                {
                    if (channels.Count > 0)
                        await DispatchAsync(alert, channels, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private void ForgetOldSends(DateTimeOffset now)
    {
        while (_sent.Count > 0 && now - _sent.Peek() >= TimeSpan.FromHours(1))
            _sent.Dequeue();
    }

    /// <summary>How long until the limit has room again (zero when it has).</summary>
    private TimeSpan TimeUntilRoom()
    {
        var now = timeProvider.GetUtcNow();
        ForgetOldSends(now);
        if (_sent.Count < Math.Max(1, options.Value.MaxPerHour))
            return TimeSpan.Zero;

        var wait = _sent.Peek() + TimeSpan.FromHours(1) - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
    }

    private async Task SayHeldBackAsync(List<INotifier> channels, CancellationToken stoppingToken)
    {
        if (_suppressed == 0)
            return;

        var max = Math.Max(1, options.Value.MaxPerHour);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var from = _firstSuppressed.UtcDateTime.ToString("HH:mm", culture);
        var to = _lastSuppressed.UtcDateTime.ToString("HH:mm", culture);
        var when = from == to ? $"at {from} UTC" : $"between {from} and {to} UTC";
        var titles = string.Join(", ", _suppressedTitles) + (_moreTitles ? " and others" : "");

        var held = new Alert("Alerts were held back",
            string.Create(culture, $"{_suppressed} alert(s) were not sent {when} because the limit of {max} an hour was reached: {titles}."));

        _suppressed = 0;
        _suppressedTitles.Clear();
        _moreTitles = false;
        _sent.Enqueue(timeProvider.GetUtcNow());
        await SendToAllAsync(held, channels, stoppingToken);
    }

    private async Task DispatchAsync(Alert alert, List<INotifier> channels, CancellationToken stoppingToken)
    {
        var now = timeProvider.GetUtcNow();
        ForgetOldSends(now);

        var max = Math.Max(1, options.Value.MaxPerHour);
        if (_sent.Count >= max)
        {
            if (_suppressed++ == 0)
                _firstSuppressed = now;

            _lastSuppressed = now;
            if (!_suppressedTitles.Contains(alert.Title))
            {
                if (_suppressedTitles.Count < TitlesInNotice)
                    _suppressedTitles.Add(alert.Title);
                else
                    _moreTitles = true;
            }

            logger.LogWarning("Alert suppressed (the limit of {Max} an hour is reached): {Title}", max, alert.Title);
            return;
        }

        await SayHeldBackAsync(channels, stoppingToken);

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
            logger.LogInformation("Alert sent to {Channel}: {Title}", channel.Name, alert.Title);
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
