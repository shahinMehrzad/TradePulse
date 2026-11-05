using System.Net;
using System.Net.Mail;
using Application.Interfaces;
using Application.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Infrastructure.Notifications;

/// <summary>
/// Mail settings of the API, only from configuration (User Secrets, environment, <c>deploy/.env</c>), never appsettings or the database:
/// <c>Email:Host</c>, <c>Port</c>, <c>UseStartTls</c>, <c>UserName</c>, <c>Password</c> and <c>From</c>. Without a host and a sender
/// address the server has no mail and email codes are not offered.
/// </summary>
public sealed class MailOptions
{
    public const string SectionName = "Email";

    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseStartTls { get; set; } = true;
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string From { get; set; } = "";

    /// <summary>
    /// How long the mail server may take before the send is given up. It has to stay below the panel's own wait for the API (15 s): a
    /// server that never answers (outbound SMTP blocked) is then reported by the API as a failed send, instead of the panel giving up first
    /// and showing its general error page.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(From);
}

public sealed class SmtpMailSender(IOptions<MailOptions> options, ILogger<SmtpMailSender> logger) : IMailSender
{
    public bool IsConfigured => options.Value.IsConfigured;

    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        var mail = options.Value;
        if (!mail.IsConfigured)
            throw new NotificationException("The server has no mail settings.");

        // 1 to 13 s: never longer than the panel waits for the API.
        var wait = TimeSpan.FromSeconds(Math.Clamp(mail.TimeoutSeconds, 1, 13));

        using var client = new SmtpClient(mail.Host, mail.Port) { EnableSsl = mail.UseStartTls, Timeout = (int)wait.TotalMilliseconds };
        if (!string.IsNullOrWhiteSpace(mail.UserName))
            client.Credentials = new NetworkCredential(mail.UserName, mail.Password);

        using var message = new MailMessage { From = new MailAddress(mail.From), Subject = subject, Body = body };
        message.To.Add(to);

        // SmtpClient.Timeout is not a promise for the async send, so the wait is also a deadline on the call itself.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(wait);

        try
        {
            await client.SendMailAsync(message, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("The mail server did not answer within {Seconds} s.", wait.TotalSeconds);
            throw new NotificationException("The mail server did not answer in time.");
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException)
        {
            // The message holds a sign-in code and the address: log what went wrong, never the mail.
            logger.LogWarning("A sign-in mail was not accepted by the mail server ({Reason}).", (ex as SmtpException)?.StatusCode.ToString() ?? ex.GetType().Name);
            throw new NotificationException("The mail server did not accept the message.");
        }
    }
}
