using Application.Notifications;
using BackgroundWorker.Alerts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace BackgroundWorker.UnitTests.Alerts;

public class AlertDispatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class RecordingChannel(string name, Exception? failure = null) : INotifier
    {
        private readonly List<Alert> _sent = [];
        public string Name => name;

        public IReadOnlyList<Alert> Sent
        {
            get
            {
                lock (_sent)
                    return [.. _sent];
            }
        }

        public Task SendAsync(Alert alert, CancellationToken cancellationToken)
        {
            if (failure is not null)
                throw failure;

            lock (_sent)
                _sent.Add(alert);
            return Task.CompletedTask;
        }
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
    private readonly AlertQueue _queue = new();

    private AlertDispatcher Dispatcher(int maxPerHour, params INotifier[] channels) =>
        new(_queue, channels, Options.Create(new AlertSettings { MaxPerHour = maxPerHour }), _time, NullLogger<AlertDispatcher>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"timed out waiting for: {what}");
            await Task.Delay(5, Ct);
        }
    }

    [Fact]
    public async Task AnAlertGoesToEveryChannel()
    {
        var telegram = new RecordingChannel("Telegram");
        var email = new RecordingChannel("Email");
        using var dispatcher = Dispatcher(20, telegram, email);
        await dispatcher.StartAsync(Ct);

        _queue.Raise(new Alert("Title", "Body"));
        await WaitUntilAsync(() => telegram.Sent.Count == 1 && email.Sent.Count == 1, "both channels");

        Assert.Equal(new Alert("Title", "Body"), telegram.Sent[0]);
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AChannelThatFailsDoesNotKeepTheOthersFromGettingTheMessageNorStopTheDispatcher()
    {
        var broken = new RecordingChannel("Telegram", new NotificationException("Telegram answered 401"));
        var crashing = new RecordingChannel("Weird", new InvalidOperationException("https://api.telegram.org/bot123:SECRET/sendMessage"));
        var email = new RecordingChannel("Email");
        using var dispatcher = Dispatcher(20, broken, crashing, email);
        await dispatcher.StartAsync(Ct);

        _queue.Raise(new Alert("First", "x"));
        _queue.Raise(new Alert("Second", "x"));
        await WaitUntilAsync(() => email.Sent.Count == 2, "the working channel to get both");

        Assert.Equal(["First", "Second"], email.Sent.Select(a => a.Title));
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MoreThanTheHourlyLimitIsHeldBackAndSaidOnceWhenTheHourIsOver()
    {
        var channel = new RecordingChannel("Telegram");
        using var dispatcher = Dispatcher(2, channel);
        await dispatcher.StartAsync(Ct);

        foreach (var title in new[] { "A", "B", "C", "D" })
            _queue.Raise(new Alert(title, "x"));
        await WaitUntilAsync(() => channel.Sent.Count == 2, "the first two");
        await Task.Delay(100, Ct);

        Assert.Equal(["A", "B"], channel.Sent.Select(a => a.Title));   // C and D were held back

        _time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        _queue.Raise(new Alert("E", "x"));
        await WaitUntilAsync(() => channel.Sent.Count == 4, "the note and the new alert");

        Assert.Equal(["Alerts were held back", "E"], channel.Sent.Skip(2).Select(a => a.Title));
        Assert.Contains("2 alert(s)", channel.Sent[2].Body);
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WithoutAnyChannelAlertsAreDroppedQuietlyAndTheQueueNeverFillsTheMemory()
    {
        using var dispatcher = Dispatcher(20);
        await dispatcher.StartAsync(Ct);

        for (var i = 0; i < 500; i++)
            _queue.Raise(new Alert("x", "y"));

        await WaitUntilAsync(() => !_queue.Reader.TryPeek(out _), "the queue to be emptied");
        await dispatcher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void RaisingNeverBlocksEvenWhenNobodyReads()
    {
        var queue = new AlertQueue();

        for (var i = 0; i < 10_000; i++)
            queue.Raise(new Alert("x", "y"));   // returns at once; what does not fit is dropped
    }
}
