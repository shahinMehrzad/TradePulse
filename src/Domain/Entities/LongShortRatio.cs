namespace Domain.Entities;
public readonly record struct LongShortRatio(DateTime Time, decimal Ratio, decimal LongAccount, decimal ShortAccount);
