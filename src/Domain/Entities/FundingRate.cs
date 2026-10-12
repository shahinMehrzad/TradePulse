namespace Domain.Entities
{
    /// <summary>One funding settlement of a perpetual futures contract.</summary>
    public readonly record struct FundingRate(DateTime FundingTime, decimal Rate, decimal MarkPrice);
}
