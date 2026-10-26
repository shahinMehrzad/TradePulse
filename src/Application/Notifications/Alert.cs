namespace Application.Notifications;

public sealed record Alert(string Title, string Body);

public interface INotifier
{
    string Name { get; }

    Task SendAsync(Alert alert, CancellationToken cancellationToken);
}

public sealed class NotificationException(string message) : Exception(message);
