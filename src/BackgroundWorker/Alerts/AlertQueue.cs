using System.Threading.Channels;
using Application.Notifications;

namespace BackgroundWorker.Alerts;

public interface IAlertSink
{
    /// <summary>Never blocks and never throws: whatever raises an alert is doing something more important.</summary>
    void Raise(Alert alert);
}

public sealed class AlertQueue : IAlertSink
{
    private readonly Channel<Alert> _channel = Channel.CreateBounded<Alert>(
        new BoundedChannelOptions(100) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    public ChannelReader<Alert> Reader => _channel.Reader;

    public void Raise(Alert alert) => _channel.Writer.TryWrite(alert);
}
