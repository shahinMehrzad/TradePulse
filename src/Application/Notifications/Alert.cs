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

/// <summary>A channel could not deliver. <see cref="RetryAfter"/> is set when the channel itself said how long to wait (Telegram's 429).</summary>
public sealed class NotificationException(string message, TimeSpan? retryAfter = null) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}
