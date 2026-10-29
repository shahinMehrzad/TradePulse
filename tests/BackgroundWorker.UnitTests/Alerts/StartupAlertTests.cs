using Application.Notifications;
using BackgroundWorker.Alerts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.UnitTests.Alerts;

public class StartupAlertTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingSink : IAlertSink
    {
        public List<Alert> Raised { get; } = [];

        public void Raise(Alert alert) => Raised.Add(alert);
    }

    private sealed class RecordingChannel : INotifier
    {
        private readonly List<Alert> _sent = [];
        public string Name => "Telegram";

        public IReadOnlyList<Alert> Sent
        {
            get
            {
                lock (_sent)
                    return [.. _sent];
            }
        }

        public Task SendAsync(Alert alert, CancellationToken cancellationToken)
        {
            lock (_sent)
                _sent.Add(alert);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task StartingSaysSoOnce()
    {
        var sink = new RecordingSink();

        await new StartupAlert(sink).StartAsync(Ct);

        Assert.Equal("Collector started", Assert.Single(sink.Raised).Title);
    }

    [Fact]
    public async Task StoppingSaysNothing()
    {
        var sink = new RecordingSink();

        await new StartupAlert(sink).StopAsync(Ct);

        Assert.Empty(sink.Raised);
    }

    [Fact]
    public async Task TheMessageReachesAChannelThroughTheDispatcherLikeAnyOtherAlert()
    {
        var queue = new AlertQueue();
        var channel = new RecordingChannel();
        using var dispatcher = new AlertDispatcher(
            queue, [channel], Options.Create(new AlertSettings()), TimeProvider.System, NullLogger<AlertDispatcher>.Instance);

        await new StartupAlert(queue).StartAsync(Ct);   // raised before the dispatcher runs: the queue holds it
        await dispatcher.StartAsync(Ct);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (channel.Sent.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the startup message was not sent");
            await Task.Delay(5, Ct);
        }

        Assert.Equal("Collector started", channel.Sent[0].Title);
        await dispatcher.StopAsync(CancellationToken.None);
    }
}
