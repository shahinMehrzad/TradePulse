namespace Domain.Enums;

/// <summary>Where a paper trade is. Stored as a number: only add members, never renumber.</summary>
public enum PaperTradeStatusEnum : byte
{
    Open = 1,
    TakeProfit = 2,
    StopLoss = 3,
    Expired = 4
}
