namespace Application.Notifications;

/// <param name="Key">What the alert is about (kind and account): at most one alert with the same key goes out every few minutes.</param>
public sealed record SecurityAlert(string Key, string Title, string Body);

/// <summary>
/// Tells the owner that somebody is trying to get into an account (Telegram). Never blocks the caller and never throws: a sign-in must not
/// wait for, or fail because of, a message. Nothing secret goes in it: no password, no code, no key.
/// </summary>
public interface ISecurityAlerts
{
    void Raise(SecurityAlert alert);
}
