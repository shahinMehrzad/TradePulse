using System.Threading.Channels;
using Application.Notifications;
using Domain.Entities;
using Domain.Enums;
using Microsoft.Extensions.Logging;

namespace Application.Signals;

/// <summary>How the announcer keeps time.</summary>
public sealed record AnnouncerOptions
{
    /// <summary>How often what is still to be told (a message that failed, an end waiting for its result) is looked for again.</summary>
    public TimeSpan Retry { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The longest the announcer waits when the chat says "too many requests"; a longer wish is waited for in several rounds.</summary>
    public TimeSpan LongestPause { get; init; } = TimeSpan.FromMinutes(2);
}

/// <summary>
/// Tells the chat about the paper trades: a message when a trade opens, and when it ends the same message edited with the result and a short
/// reply under it. Everything is done by one task, in order, and nothing here is ever waited for by the watch or by the webhook: they put a
/// trade in a queue and go on.
/// <para>
/// Nothing is lost when the chat or the exchange does not answer. What is still to be told is also in the database (a trade that is open
/// and has no message, a trade that ended and whose end is not marked as told), and is looked for again every
/// <see cref="AnnouncerOptions.Retry"/> and at the start. An end waits for its result (<see cref="TradeSettlement"/>: the exchange's funding)
/// so that the message never shows half of it.
/// </para>
/// </summary>
public sealed class TradeAnnouncer(
    ISignalStore store, ITradeChat chat, TradeSettlement settlement, AnnouncerOptions options, TimeProvider time, ILogger<TradeAnnouncer> logger) : ITradeEvents, IOpenedTradeSink
{
    private enum Kind { Opened, Ended, Tick }

    private readonly record struct Work(Kind Kind, PaperTrade? Trade);

    private readonly Channel<Work> _queue = Channel.CreateUnbounded<Work>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<int> _openTold = [];
    private readonly HashSet<int> _endTold = [];
    private DateTime _pausedUntil;

    /// <summary>A trade has just opened and is stored: its message is sent soon. Never waits.</summary>
    public void Opened(PaperTrade trade) => _queue.Writer.TryWrite(new Work(Kind.Opened, trade));

    /// <summary>A trade has ended and is stored as ended (called by the watch): its end is told soon. Never waits.</summary>
    public Task OnClosedAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        _queue.Writer.TryWrite(new Work(Kind.Ended, trade));
        return Task.CompletedTask;
    }

    /// <summary>Runs until cancelled. Nothing but cancellation ends it.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CatchUpAsync(cancellationToken);

            using var timer = new PeriodicTimer(options.Retry, time);
            var ticks = Task.Run(async () =>
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                    _queue.Writer.TryWrite(new Work(Kind.Tick, null));
            }, cancellationToken);

            await foreach (var work in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                switch (work.Kind)
                {
                    case Kind.Opened:
                        await AnnounceOpenedAsync(work.Trade!, cancellationToken);
                        break;
                    case Kind.Ended:
                        await AnnounceEndAsync(work.Trade!, cancellationToken);
                        break;
                    default:
                        await CatchUpAsync(cancellationToken);
                        break;
                }
            }

            await ticks;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The normal way out.
        }
    }

    /// <summary>Everything the database says is still to be told, oldest first.</summary>
    private async Task CatchUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var trade in await store.GetOpenWithoutMessageAsync(cancellationToken))
                await AnnounceOpenedAsync(trade, cancellationToken);

            foreach (var trade in await store.GetEndsToAnnounceAsync(cancellationToken))
                await AnnounceEndAsync(trade, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("What is still to be told could not be read ({Error}). Looking again later", ex.GetType().Name);
        }
    }

    // ---- the message when a trade opens --------------------------------------------------------------------------------------

    private async Task AnnounceOpenedAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        // A trade may be here twice (handed over, and found in the database): its message is sent once.
        if (trade.TelegramMessageId is not null || _openTold.Contains(trade.Id) || Paused())
            return;

        try
        {
            var sent = await chat.SendAsync(TradeMessages.Opened(trade), cancellationToken);
            _openTold.Add(trade.Id);
            trade.AttachMessage(sent.ChatId, sent.MessageId);
            await store.AttachMessageAsync(trade.Id, sent.ChatId, sent.MessageId, cancellationToken);
            await LogAsync(trade, SignalEventKindEnum.MessageSent, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailedAsync(trade, "the message of the opened trade", ex, cancellationToken);
        }
    }

    // ---- the message when it ends -------------------------------------------------------------------------------------------

    private async Task AnnounceEndAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        if (trade.EndAnnouncedAt is not null || trade.IsOpen || _endTold.Contains(trade.Id) || Paused())
            return;

        try
        {
            // The result comes first: the message shows all of it or waits.
            if (trade.NetResult is null)
            {
                var outcome = await settlement.SettleAsync(trade, cancellationToken);
                if (outcome is SettlementOutcome.Postponed or SettlementOutcome.Failed)
                    return;   // found again at the next look
            }

            await TellEndAsync(trade, cancellationToken);
            trade.MarkEndAnnounced(time.GetUtcNow().UtcDateTime);
            _endTold.Add(trade.Id);
            await store.MarkEndAnnouncedAsync(trade.Id, trade.EndAnnouncedAt!.Value, cancellationToken);
            await LogAsync(trade, SignalEventKindEnum.MessageEdited, null, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await FailedAsync(trade, "the end of the trade", ex, cancellationToken);
        }
    }

    private async Task TellEndAsync(PaperTrade trade, CancellationToken cancellationToken)
    {
        var text = TradeMessages.Ended(trade);

        if (trade is { TelegramChatId: { } chatId, TelegramMessageId: { } messageId })
        {
            var message = new ChatMessage(chatId, messageId);
            if (await chat.EditAsync(message, text, cancellationToken))
            {
                await chat.ReplyAsync(message, TradeMessages.EndedReply(trade), cancellationToken);
                return;
            }

            logger.LogWarning("The message of the trade {Id} is gone: the end is sent as a new message", trade.Id);
        }

        // It never had a message, or it was deleted: the whole story in one new message (the result in it already says what happened).
        await chat.SendAsync(text, cancellationToken);
    }

    // ---- the rest --------------------------------------------------------------------------------------------------------------

    private bool Paused() => time.GetUtcNow().UtcDateTime < _pausedUntil;

    private async Task FailedAsync(PaperTrade trade, string what, Exception ex, CancellationToken cancellationToken)
    {
        if (ex is NotificationException { RetryAfter: { } wait })
        {
            _pausedUntil = time.GetUtcNow().UtcDateTime + (wait < options.LongestPause ? wait : options.LongestPause);
            logger.LogWarning("The chat asked to wait {Seconds:0} s before {What} of the trade {Id}", wait.TotalSeconds, what, trade.Id);
        }
        else
        {
            // A NotificationException is written to be told (it keeps the token out of it); any other exception may carry the address it called.
            logger.LogWarning("{What} of the trade {Id} could not be told ({Error}). Trying again", what, trade.Id, ex is NotificationException ? ex.Message : ex.GetType().Name);
        }

        await LogAsync(trade, SignalEventKindEnum.MessageFailed, ex is NotificationException ? ex.Message : ex.GetType().Name, cancellationToken);
    }

    private async Task LogAsync(PaperTrade trade, SignalEventKindEnum kind, string? detail, CancellationToken cancellationToken)
    {
        try
        {
            await store.AddEventAsync(new SignalEvent { SignalId = trade.SignalId, At = time.GetUtcNow().UtcDateTime, Kind = kind, Detail = detail }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("The log of the trade {Id} could not be written ({Error})", trade.Id, ex.GetType().Name);
        }
    }
}
