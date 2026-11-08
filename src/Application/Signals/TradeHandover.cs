using Domain.Entities;

namespace Application.Signals;

/// <summary>Whoever follows the price of the open paper trades: told about a trade that has just opened. Never waits.</summary>
public interface ITradeTracker
{
    void Track(PaperTrade trade);
}

/// <summary>Whoever tells the chat about the paper trades: told about a trade that has just opened. Never waits.</summary>
public interface IOpenedTradeSink
{
    void Opened(PaperTrade trade);
}
