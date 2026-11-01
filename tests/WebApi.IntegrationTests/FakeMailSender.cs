using System.Text.RegularExpressions;
using Application.Interfaces;
using Application.Notifications;

namespace WebApi.IntegrationTests;

/// <summary>Collects the mails the API would send, so a test can read the sign-in code a user would find in their mailbox.</summary>
public sealed partial class FakeMailSender : IMailSender
{
    public record Mail(string To, string Subject, string Body);

    private readonly List<Mail> _sent = [];

    public bool IsConfigured { get; set; } = true;

    /// <summary>Makes the next sends fail the way a refusing mail server does.</summary>
    public bool Fails { get; set; }

    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        if (Fails)
            throw new NotificationException("The mail server did not accept the message.");

        lock (_sent)
            _sent.Add(new Mail(to, subject, body));
        return Task.CompletedTask;
    }

    public int CountTo(string address)
    {
        lock (_sent)
            return _sent.Count(m => m.To == address);
    }

    /// <summary>The 6-digit code in the newest mail to <paramref name="address"/>.</summary>
    public string LastCodeTo(string address)
    {
        lock (_sent)
        {
            var mail = _sent.Last(m => m.To == address);
            return CodePattern().Match(mail.Body).Groups[1].Value;
        }
    }

    [GeneratedRegex(@"code is (\d{6})")]
    private static partial Regex CodePattern();
}
