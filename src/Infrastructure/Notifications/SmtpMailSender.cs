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

        using var client = new SmtpClient(mail.Host, mail.Port) { EnableSsl = mail.UseStartTls, Timeout = 15_000 };
        if (!string.IsNullOrWhiteSpace(mail.UserName))
            client.Credentials = new NetworkCredential(mail.UserName, mail.Password);

        using var message = new MailMessage { From = new MailAddress(mail.From), Subject = subject, Body = body };
        message.To.Add(to);

        try
        {
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException)
        {
            // The message holds a sign-in code and the address: log what went wrong, never the mail.
            logger.LogWarning("A sign-in mail was not accepted by the mail server ({Reason}).", (ex as SmtpException)?.StatusCode.ToString() ?? ex.GetType().Name);
            throw new NotificationException("The mail server did not accept the message.");
        }
    }
}
