using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace BackgroundWorker.Alerts;

public sealed class AlertHealthPublisher(IAlertSink sink, IOptions<AlertSettings> options) : IHealthCheckPublisher
{
    private readonly HealthAlertTracker _tracker = new(Math.Max(1, options.Value.ConfirmationsToAlert));

    public Task PublishAsync(HealthReport report, CancellationToken cancellationToken)
    {
        if (_tracker.Observe(report) is { } alert)
            sink.Raise(alert);

        return Task.CompletedTask;
    }
}
