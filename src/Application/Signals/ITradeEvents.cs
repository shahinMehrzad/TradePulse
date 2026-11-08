using Domain.Entities;

namespace Application.Signals;

/// <summary>What the rest of the service wants to know about the paper trades the watch follows.</summary>
public interface ITradeEvents
{
    /// <summary>
    /// A trade has ended and is stored as ended. Called once per trade by this process; the message and the result are made from here. It
    /// must not hold up the watch for long, and a failure is logged and does not stop the watch.
    /// </summary>
    Task OnClosedAsync(PaperTrade trade, CancellationToken cancellationToken);
}

/// <summary>Nobody is listening yet.</summary>
public sealed class NoTradeEvents : ITradeEvents
{
    public static readonly NoTradeEvents Instance = new();

    public Task OnClosedAsync(PaperTrade trade, CancellationToken cancellationToken) => Task.CompletedTask;
}
