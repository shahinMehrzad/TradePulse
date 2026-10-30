namespace Application.Interfaces;

/// <summary>Sends one mail to one person. Used for the sign-in codes; the collector's alerts have their own channel.</summary>
public interface IMailSender
{
    /// <summary>False when the server has no mail settings: email codes are then not offered at all.</summary>
    bool IsConfigured { get; }

    /// <exception cref="Application.Notifications.NotificationException">The mail server did not take the message.</exception>
    Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken);
}
