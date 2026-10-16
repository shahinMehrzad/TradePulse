namespace Application.Dtos.Candles;

/// <summary>One closed candle as returned by the read API. Times are UTC.</summary>
public readonly record struct CandleResponse(
    DateTime OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume,
    DateTime CloseTime, decimal QuoteAssetVolume, long NumberOfTrades, decimal TakerBuyBaseVolume, decimal TakerBuyQuoteVolume);

/// <param name="NextFrom">Pass as <c>from</c> to get the following page; null when this was the last page.</param>
public sealed record CandlePage(IReadOnlyList<CandleResponse> Items, DateTime? NextFrom);
