namespace Application.Signals;

/// <summary>Where a message is in a chat: enough to edit it or to answer it later.</summary>
public readonly record struct ChatMessage(long ChatId, int MessageId);

/// <summary>
/// The chat the paper trades are told in. A trade has one message, sent when it opens and edited when it ends, and a short reply under it
/// when it ends (an edit makes no sound, a reply does). Text is HTML in the chat's own subset (bold, nothing else). A failure is a
/// <see cref="Notifications.NotificationException"/>, whose <see cref="Notifications.NotificationException.RetryAfter"/> is set when the chat
/// said how long to wait; it never carries a secret.
/// </summary>
public interface ITradeChat
{
    Task<ChatMessage> SendAsync(string html, CancellationToken cancellationToken);

    /// <summary>A message under <paramref name="to"/>, which makes the chat notify.</summary>
    Task<ChatMessage> ReplyAsync(ChatMessage to, string html, CancellationToken cancellationToken);

    /// <summary>Changes the text of a message. Saying the same words again is a success. False when the message is gone (deleted, or too old to edit).</summary>
    Task<bool> EditAsync(ChatMessage message, string html, CancellationToken cancellationToken);
}
