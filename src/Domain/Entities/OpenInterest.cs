namespace Domain.Entities
{
    /// <summary>Open interest snapshot: contracts (<paramref name="SumOpenInterest"/>) and their quote value.</summary>
    public readonly record struct OpenInterest(DateTime Time, decimal SumOpenInterest, decimal SumOpenInterestValue);
}
