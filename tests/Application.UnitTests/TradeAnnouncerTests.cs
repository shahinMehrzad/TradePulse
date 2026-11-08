using Application.Signals;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using static Application.UnitTests.SignalTestData;

namespace Application.UnitTests;

/// <summary>Telling the chat: a message when a trade opens, the same message edited and a reply when it ends, and nothing lost when the chat is down.</summary>
public class TradeAnnouncerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Opened.AddHours(6)));   // 14:00:02
    private readonly FakeSignalStore _store = new();
    private readonly FakeTradeChat _chat = new();
    private readonly FakeFundingGateway _funding = new();
    private readonly FakeFollowedSymbols _symbols = new(Btc);
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

    private TradeAnnouncer NewAnnouncer() => new(
        _store, _chat, new TradeSettlement(_funding, _store, _symbols, new SettlementOptions(), _time, NullLogger<TradeSettlement>.Instance),
        new AnnouncerOptions(), _time, NullLogger<TradeAnnouncer>.Instance);

    private TradeAnnouncer? _announcer;

    private Task Run()
    {
        _announcer = NewAnnouncer();
        return Task.Run(() => _announcer.RunAsync(_stop.Token), Ct);
    }

    private async Task StopAsync(Task run)
    {
        _stop.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, $"Timed out waiting for: {what}");
            await Task.Delay(10, Ct);
        }
    }

    /// <summary>Moves the clock in steps of the retry time, giving the announcer a moment after each.</summary>
    private async Task PassAsync(int steps)
    {
        for (var i = 0; i < steps; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(30));
            await Task.Delay(40, Ct);
        }
    }

    /// <summary>A trade that ended at its target a few hours ago, with the message it was opened with, stored as ended.</summary>
    private async Task<PaperTrade> EndedAsync(int id = 1, bool withMessage = true, DateTime? endedAt = null)
    {
        var trade = Long(id: id);
        trade.Observe(104.5m, endedAt ?? Opened.AddHours(5).AddMinutes(15));
        if (withMessage)
            trade.AttachMessage(-1001, 55 + id);

        await _store.CloseAsync(trade, Ct);
        return trade;
    }

    // ---- when a trade opens ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheMessageOfAnOpenedTradeIsSentAndRemembered()
    {
        var trade = Long();
        _store.Open.Add(trade);
        var announcer = NewAnnouncer();
        announcer.Opened(trade);
        _announcer = announcer;
        var run = Task.Run(() => announcer.RunAsync(_stop.Token), Ct);

        await WaitForAsync(() => _chat.Sent.Count == 1 && _store.Attached.Count == 1, "the message");

        Assert.Equal(TradeMessages.Opened(trade), _chat.Sent[0].Text);
        Assert.Equal((-1001L, _chat.Sent[0].Message.MessageId), _store.Attached[trade.Id]);
        Assert.Equal(_chat.Sent[0].Message.MessageId, trade.TelegramMessageId);
        Assert.Equal(SignalEventKindEnum.MessageSent, Assert.Single(_store.Events).Kind);
        await StopAsync(run);
    }

    [Fact]
    public async Task ATradeThatHasNoMessageWhenTheServiceStartsGetsOne()
    {
        _store.Open.Add(Long());

        var run = Run();
        await WaitForAsync(() => _chat.Sent.Count == 1, "the message found in the database");

        Assert.Equal(TradeMessages.Opened(_store.Open[0]), _chat.Sent[0].Text);
        await StopAsync(run);
    }

    [Fact]
    public async Task AMessageThatCouldNotBeSentIsSentAtTheNextLookAndTheFailureIsLogged()
    {
        _chat.SendFailures = 1;
        var trade = Long();
        _store.Open.Add(trade);
        var run = Run();
        await WaitForAsync(() => _store.Events.Any(e => e.Kind == SignalEventKindEnum.MessageFailed), "the failure in the log");
        Assert.Empty(_chat.Sent);

        await PassAsync(1);
        await WaitForAsync(() => _chat.Sent.Count == 1, "the second try");

        var failed = _store.Events.First(e => e.Kind == SignalEventKindEnum.MessageFailed);
        Assert.Equal(trade.SignalId, failed.SignalId);
        Assert.Contains("could not be reached", failed.Detail);
        await StopAsync(run);
    }

    [Fact]
    public async Task ATradeThatIsHandedOverAndFoundInTheDatabaseIsToldOnce()
    {
        var trade = Long();
        _store.Open.Add(trade);
        var announcer = NewAnnouncer();
        announcer.Opened(trade);
        announcer.Opened(trade);
        var run = Task.Run(() => announcer.RunAsync(_stop.Token), Ct);

        await WaitForAsync(() => _chat.Sent.Count >= 1, "the message");
        await PassAsync(2);

        Assert.Single(_chat.Sent);
        await StopAsync(run);
    }

    [Fact]
    public async Task HandingATradeOverDoesNotWaitForTheChat()
    {
        _chat.SendFailures = 100;
        var announcer = NewAnnouncer();

        announcer.Opened(Long());
        await announcer.OnClosedAsync(await EndedAsync(2), Ct);

        Assert.Equal(0, _chat.Attempts);   // nothing is sent until the announcer runs
    }

    // ---- when it ends ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheEndEditsTheSameMessageAndSendsAShortReply()
    {
        var trade = await EndedAsync();
        _funding.Add(new FundingRate(Opened.AddHours(2), 0.0001m, 101m));
        var run = Run();
        await _announcer!.OnClosedAsync(trade, Ct);

        await WaitForAsync(() => _store.EndsTold.Count == 1, "the end to be told");

        var edit = Assert.Single(_chat.Edits);
        Assert.Equal(new ChatMessage(-1001, 56), edit.Message);
        Assert.Equal(TradeMessages.Ended(trade), edit.Text);
        var reply = Assert.Single(_chat.Replies);
        Assert.Equal(new ChatMessage(-1001, 56), reply.To);
        Assert.Equal(TradeMessages.EndedReply(trade), reply.Text);
        Assert.Contains(_store.Events, e => e.Kind == SignalEventKindEnum.MessageEdited);
        Assert.Same(trade, Assert.Single(_store.Settled));
        await StopAsync(run);
    }

    [Fact]
    public async Task AnEndIsToldOnceWhetherItWasHandedOverOrFoundInTheDatabase()
    {
        var trade = await EndedAsync();
        var run = Run();
        await _announcer!.OnClosedAsync(trade, Ct);
        await _announcer.OnClosedAsync(trade, Ct);

        await WaitForAsync(() => _store.EndsTold.Count == 1, "the end to be told");
        await PassAsync(2);

        Assert.Single(_chat.Edits);
        Assert.Single(_chat.Replies);
        await StopAsync(run);
    }

    [Fact]
    public async Task AnEndThatWasLeftBeforeARestartIsToldWhenTheServiceStarts()
    {
        await EndedAsync();

        var run = Run();
        await WaitForAsync(() => _chat.Edits.Count == 1 && _chat.Replies.Count == 1, "the end found in the database");

        Assert.Single(_store.EndsTold);
        await StopAsync(run);
    }

    [Fact]
    public async Task TheEndWaitsForItsResultWhileTheExchangeDoesNotAnswerAndIsToldAsSoonAsItDoes()
    {
        _funding.Fail = true;
        var trade = await EndedAsync();
        var run = Run();
        await _announcer!.OnClosedAsync(trade, Ct);
        await WaitForAsync(() => _funding.Calls.Count >= 1, "the first, failing, try");
        Assert.Empty(_chat.Edits);
        Assert.Null(trade.NetResult);

        _funding.Fail = false;
        await PassAsync(1);
        await WaitForAsync(() => _chat.Edits.Count == 1, "the end, once the result is known");

        Assert.NotNull(trade.NetResult);
        Assert.Contains("Net result", _chat.Edits[0].Text);
        await StopAsync(run);
    }

    [Fact]
    public async Task AnEndRightAfterAFundingMomentWaitsForTheSettlementToBePublished()
    {
        // The trade ended at 16:00:02; the clock is at 16:00:12. Its result waits until 16:02.
        _time.SetUtcNow(new DateTimeOffset(Opened.AddHours(8).AddSeconds(10)));
        var trade = await EndedAsync(endedAt: Opened.AddHours(8));
        var run = Run();
        await _announcer!.OnClosedAsync(trade, Ct);
        await PassAsync(1);   // 16:00:42
        Assert.Empty(_chat.Edits);

        await PassAsync(4);   // 16:02:42

        await WaitForAsync(() => _chat.Edits.Count == 1, "the end after the lag");
        await StopAsync(run);
    }

    [Fact]
    public async Task AMessageThatWasDeletedIsReplacedByANewOneWithTheWholeStory()
    {
        _chat.MessageGone = true;
        var trade = await EndedAsync();
        var run = Run();
        await _announcer!.OnClosedAsync(trade, Ct);

        await WaitForAsync(() => _store.EndsTold.Count == 1, "the end to be told");

        Assert.Empty(_chat.Edits);
        Assert.Empty(_chat.Replies);
        Assert.Equal(TradeMessages.Ended(trade), Assert.Single(_chat.Sent).Text);
        await StopAsync(run);
    }

    [Fact]
    public async Task ATradeThatNeverHadAMessageGetsTheWholeStoryAsANewOne()
    {
        var trade = await EndedAsync(withMessage: false);
        var run = Run();
        await _announcer!.OnClosedAsync(trade, Ct);

        await WaitForAsync(() => _store.EndsTold.Count == 1, "the end to be told");

        Assert.Equal(TradeMessages.Ended(trade), Assert.Single(_chat.Sent).Text);
        Assert.Empty(_chat.Edits);
        await StopAsync(run);
    }

    [Fact]
    public async Task AnEndThatTheChatRefusedIsToldAtTheNextLook()
    {
        await EndedAsync(withMessage: false);   // found in the database when the service starts
        _chat.SendFailures = 1;
        var run = Run();
        await WaitForAsync(() => _store.Events.Any(e => e.Kind == SignalEventKindEnum.MessageFailed), "the failure");
        Assert.Empty(_store.EndsTold);

        await PassAsync(1);

        await WaitForAsync(() => _store.EndsTold.Count == 1, "the end after the retry");
        await StopAsync(run);
    }

    // ---- the chat asks to wait -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WhenTheChatAsksToWaitNothingIsSentUntilThatTimeHasPassed()
    {
        _chat.RetryAfterOnce = TimeSpan.FromSeconds(45);
        _store.Open.Add(Long());
        var run = Run();
        await WaitForAsync(() => _store.Events.Any(e => e.Kind == SignalEventKindEnum.MessageFailed), "the refused try, logged (the wait is set before)");

        await PassAsync(1);   // 30 s after: still within the 45 s
        Assert.Equal(1, _chat.Attempts);

        await PassAsync(1);   // 60 s after
        await WaitForAsync(() => _chat.Sent.Count == 1, "the message after the wait");
        await StopAsync(run);
    }

    // ---- the log ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task ALogThatCannotBeWrittenDoesNotStopTheMessages()
    {
        _store.EventsFail = true;
        _store.Open.Add(Long());

        var run = Run();
        await WaitForAsync(() => _chat.Sent.Count == 1, "the message");

        Assert.Empty(_store.Events);
        await StopAsync(run);
    }

    [Fact]
    public async Task StoppingEndsTheLoopWithoutTellingAnythingMore()
    {
        var run = Run();

        await StopAsync(run);

        Assert.Equal(0, _chat.Attempts);
    }
}
