using System.Net;
using System.Net.Mail;
using Application.Notifications;
using Microsoft.Extensions.Options;

namespace Infrastructure.Notifications;

public interface IEmailTransport
{
    Task SendAsync(string subject, string body, CancellationToken cancellationToken);
}

public sealed class EmailNotifier(IEmailTransport transport, IOptions<AlertsOptions> options) : INotifier
{
    public string Name => "Email";

    public Task SendAsync(Alert alert, CancellationToken cancellationToken)
    {
        var label = options.Value.Label;
        return transport.SendAsync($"[TradePulse] {label}: {alert.Title}", $"{alert.Body}\n\n-- {label}", cancellationToken);
    }
}

public sealed class SmtpEmailTransport(IOptions<AlertsOptions> options) : IEmailTransport
{
    public async Task SendAsync(string subject, string body, CancellationToken cancellationToken)
    {
        var email = options.Value.Email;

        using var client = new SmtpClient(email.Host, email.Port) { EnableSsl = email.UseStartTls, Timeout = 15_000 };
        if (!string.IsNullOrWhiteSpace(email.UserName))
            client.Credentials = new NetworkCredential(email.UserName, email.Password);

        using var message = new MailMessage { From = new MailAddress(email.From), Subject = subject, Body = body };
        foreach (var to in email.To.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            message.To.Add(to);

        try
        {
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (SmtpException ex)
        {
            throw new NotificationException($"The mail server did not accept the message ({ex.StatusCode})");
        }
    }
}
