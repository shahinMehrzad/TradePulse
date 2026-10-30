namespace Domain.Entities;

public readonly record struct TakerVolume(DateTime Time, decimal BuySellRatio, decimal BuyVolume, decimal SellVolume);
