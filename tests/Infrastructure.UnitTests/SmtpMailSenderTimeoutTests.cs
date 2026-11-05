using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Application.Notifications;
using Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Infrastructure.UnitTests;

/// <summary>
/// A mail server that takes the connection and never says a word (what outbound SMTP blocked by the hosting provider looks like) must end
/// as a failed send well before the panel gives up on the API, not hang until the panel shows its general error page.
/// </summary>
public class SmtpMailSenderTimeoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SmtpMailSender Sender(int port, int timeoutSeconds) => new(
        Options.Create(new MailOptions
        {
            Host = "127.0.0.1", Port = port, UseStartTls = false, From = "tp@example.com", TimeoutSeconds = timeoutSeconds,
        }),
        NullLogger<SmtpMailSender>.Instance);

    [Fact]
    public async Task AServerThatNeverAnswersIsAFailedSendWithinTheTimeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // Takes the connection and keeps it open without sending the 220 greeting.
            var accepted = listener.AcceptTcpClientAsync(Ct).AsTask();

            var clock = Stopwatch.StartNew();
            await Assert.ThrowsAsync<NotificationException>(() => Sender(port, timeoutSeconds: 1).SendAsync("a@example.com", "subject", "body", Ct));
            clock.Stop();

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(8), $"took {clock.Elapsed}");
            (await accepted).Dispose();
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ACancelledCallStaysACancelledCallAndIsNotMistakenForATimeout()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            await cancelled.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => Sender(((IPEndPoint)listener.LocalEndpoint).Port, timeoutSeconds: 5).SendAsync("a@example.com", "subject", "body", cancelled.Token));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(600)]
    public async Task ANonsenseTimeoutIsKeptInsideTheRangeTheApiCanAfford(int seconds)
    {
        // Only that it does not throw ArgumentOutOfRange from SmtpClient: a refused connection ends as the failure it is.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        await Assert.ThrowsAsync<NotificationException>(() => Sender(port, seconds).SendAsync("a@example.com", "subject", "body", Ct));
    }
}
