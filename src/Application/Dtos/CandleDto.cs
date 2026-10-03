namespace Application.Dtos
{
    public readonly record struct CandleDto(DateTime OpenTime, decimal OpenPrice, decimal HighPrice, decimal LowPrice, decimal ClosePrice,
        decimal Volume, DateTime CloseTime, decimal QuoteAssetVolume, int NumberOfTrades, decimal TakerBuyBaseVolume, decimal TakerBuyQuoteVolume);
}
