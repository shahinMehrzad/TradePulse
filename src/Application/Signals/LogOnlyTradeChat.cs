using Microsoft.Extensions.Logging;

namespace Application.Signals;

/// <summary>
/// The chat when none is set up (no bot token or chat id): the messages are only written to the log, like the alerts of the other programs
/// without a channel. Nothing is sent, so the trades still count as told and the service does not retry them for ever.
/// </summary>
public sealed class LogOnlyTradeChat(ILogger<LogOnlyTradeChat> logger) : ITradeChat
{
    private int _nextId;

    public Task<ChatMessage> SendAsync(string html, CancellationToken cancellationToken)
    {
        var message = new ChatMessage(0, Interlocked.Increment(ref _nextId));
        logger.LogInformation("Message {Id} (no chat is set up):\n{Text}", message.MessageId, html);
        return Task.FromResult(message);
    }

    public Task<ChatMessage> ReplyAsync(ChatMessage to, string html, CancellationToken cancellationToken)
    {
        logger.LogInformation("Reply to message {To} (no chat is set up):\n{Text}", to.MessageId, html);
        return Task.FromResult(new ChatMessage(0, Interlocked.Increment(ref _nextId)));
    }

    public Task<bool> EditAsync(ChatMessage message, string html, CancellationToken cancellationToken)
    {
        logger.LogInformation("Message {Id} is now (no chat is set up):\n{Text}", message.MessageId, html);
        return Task.FromResult(true);
    }
}
