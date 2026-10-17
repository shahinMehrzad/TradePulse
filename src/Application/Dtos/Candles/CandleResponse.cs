using System.ComponentModel;

namespace Application.Dtos.Candles;

/// <summary>One closed candle as returned by the read API. Times are UTC.</summary>
public readonly record struct CandleResponse(
    DateTime OpenTime, decimal Open, decimal High, decimal Low, decimal Close, decimal Volume,
    DateTime CloseTime, decimal QuoteAssetVolume, long NumberOfTrades, decimal TakerBuyBaseVolume, decimal TakerBuyQuoteVolume);

/// <param name="NextFrom">Pass as <c>from</c> to get the following page; null when this was the last page.</param>
[ImmutableObject(true)] // tells HybridCache it may hand out the stored instance instead of deserializing a copy per hit
public sealed record CandlePage(IReadOnlyList<CandleResponse> Items, DateTime? NextFrom);

/// <summary>
/// What the cache stores for a "latest N" answer. A bare array would be deserialized on every in-memory hit, because
/// HybridCache can only share instances of types it knows are immutable.
/// </summary>
[ImmutableObject(true)]
public sealed record CandleWindow(CandleResponse[] Items);
