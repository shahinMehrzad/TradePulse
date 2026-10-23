namespace BackgroundWorker.Collection.Candles;

/// <summary>
/// Whether the collector listens to the exchange's live candle stream. (The exchange's request budget is a different matter: it
/// belongs to the exchange adapter, <c>BinanceExchangeConfigurations</c>.) Both are read from the same configuration section.
/// </summary>
public class LiveCandleSettings
{
    public const string SectionName = "BinanceExchangeConfigurations";

    /// <summary>Receive closed candles from the WebSocket the moment they close. The REST loop runs either way as the safety net.</summary>
    public bool LiveCandles { get; set; } = true;
}
