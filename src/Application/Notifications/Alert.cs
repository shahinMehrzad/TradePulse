namespace Application.Notifications;

/// <summary>
/// What the admin is told. <paramref name="Title"/> and <paramref name="Body"/> are plain text, for every channel. <paramref name="Html"/> is the
/// same words with Telegram's formatting (bold, emoji): a channel that understands it uses it, the others ignore it.
/// </summary>
public sealed record Alert(string Title, string Body, string? Html = null);

public interface INotifier
{
    string Name { get; }

    Task SendAsync(Alert alert, CancellationToken cancellationToken);
}

public sealed class NotificationException(string message) : Exception(message);
