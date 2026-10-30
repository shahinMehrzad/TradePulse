using Application.Notifications;
using Microsoft.Extensions.Hosting;

namespace BackgroundWorker.Alerts;

public sealed class StartupAlert(IAlertSink sink) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        sink.Raise(new Alert("Collector started", "The collector has started and alerts are on."));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
