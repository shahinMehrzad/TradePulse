using System.Net;
using System.Net.Sockets;
using System.Text;
using Application.Notifications;
using Infrastructure.Notifications;
using Microsoft.Extensions.Options;

namespace Infrastructure.UnitTests;

/// <summary>The real SmtpClient against a small SMTP server in this process: what really goes over the wire, no mail provider needed.</summary>
public class SmtpEmailTransportTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class FakeSmtpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly Task _serving;
        private readonly bool _refuseRecipients;

        public FakeSmtpServer(bool refuseRecipients = false)
        {
            _refuseRecipients = refuseRecipients;
            _listener.Start();
            _serving = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<string> Recipients { get; } = [];

        public string? Message { get; private set; }

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };

                await writer.WriteLineAsync("220 fake ready");
                var data = new StringBuilder();
                var inData = false;

                while (await reader.ReadLineAsync() is { } line)
                {
                    if (inData)
                    {
                        if (line == ".")
                        {
                            inData = false;
                            Message = data.ToString();
                            await writer.WriteLineAsync("250 queued");
                        }
                        else
                        {
                            data.AppendLine(line);
                        }

                        continue;
                    }

                    if (line.StartsWith("RCPT TO", StringComparison.OrdinalIgnoreCase))
                    {
                        if (_refuseRecipients)
                        {
                            await writer.WriteLineAsync("550 no such user");
                        }
                        else
                        {
                            Recipients.Add(line[8..].Trim());
                            await writer.WriteLineAsync("250 OK");
                        }
                    }
                    else if (line == "DATA")
                    {
                        inData = true;
                        await writer.WriteLineAsync("354 go ahead");
                    }
                    else if (line == "QUIT")
                    {
                        await writer.WriteLineAsync("221 bye");
                        break;
                    }
                    else
                    {
                        await writer.WriteLineAsync(line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase) ? "250 fake" : "250 OK");
                    }
                }
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or IOException)
            {
                // The test ended before a client came, or hung up.
            }
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            await _serving;
        }
    }

    private static SmtpEmailTransport Transport(int port, string to) => new(Options.Create(new AlertsOptions
    {
        InstanceName = "box",
        Email = new EmailOptions { Host = "127.0.0.1", Port = port, UseStartTls = false, From = "tradepulse@example.com", To = to },
    }));

    private static string BodyOf(string message)
    {
        var parts = message.Split("\r\n\r\n", 2);
        return parts[0].Contains("Content-Transfer-Encoding: base64", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Replace("\r\n", "")))
            : parts[1];
    }

    [Fact]
    public async Task TheMailGoesToEveryAddressWithTheSubjectAndABodyThatKeepsItsEmoji()
    {
        await using var server = new FakeSmtpServer();
        var body = "🔴 database: unhealthy - Database unreachable\n✅ candle-series: healthy again";

        await Transport(server.Port, "a@example.com; b@example.com , c@example.com")
            .SendAsync("[TradePulse] box: Collector problem", body, Ct);

        Assert.Equal(["<a@example.com>", "<b@example.com>", "<c@example.com>"], server.Recipients);
        Assert.Contains("Subject: [TradePulse] box: Collector problem", server.Message);
        Assert.Contains("charset=utf-8", server.Message);
        Assert.Equal(body, BodyOf(server.Message!).ReplaceLineEndings("\n").TrimEnd('\n'));
    }

    [Fact]
    public async Task APlainAsciiMessageStaysReadable()
    {
        await using var server = new FakeSmtpServer();

        await Transport(server.Port, "a@example.com").SendAsync("[TradePulse] box: Collector started", "The collector has started.", Ct);

        Assert.Contains("From: tradepulse@example.com", server.Message);
        Assert.Contains("The collector has started.", server.Message);
    }

    [Fact]
    public async Task AMailServerThatRefusesTheAddressIsANotificationFailureThatDoesNotNameTheServer()
    {
        await using var server = new FakeSmtpServer(refuseRecipients: true);

        var error = await Assert.ThrowsAsync<NotificationException>(
            () => Transport(server.Port, "a@example.com").SendAsync("subject", "body", Ct));

        Assert.StartsWith("The mail server did not accept the message", error.Message);
        Assert.DoesNotContain("127.0.0.1", error.Message);
    }
}
