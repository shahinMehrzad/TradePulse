using Application.Notifications;

namespace Research;

/// <summary>Prints the alert. Always there, so a watch without any channel configured is still seen working.</summary>
internal sealed class ConsoleNotifier : INotifier
{
    public string Name => "console";

    public Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        Console.WriteLine($"{DateTime.UtcNow:HH:mm:ss}Z  {alert.Title}");
        foreach (var line in alert.Body.Split(Environment.NewLine))
            Console.WriteLine($"          {line}");

        return Task.CompletedTask;
    }
}
