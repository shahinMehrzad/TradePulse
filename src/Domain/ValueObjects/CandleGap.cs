namespace Domain.ValueObjects
{    
    public readonly record struct CandleGap(DateTime FirstMissingOpenTime, DateTime LastMissingOpenTime, bool BeforeFirstCandle = false);
}
