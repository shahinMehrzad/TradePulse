namespace Infrastructure.Configurations
{
    public class BinanceExchangeConfigurations
    {
        public Dictionary<string, BinanceSymbolConfigurations> Symbols { get; set; } = new Dictionary<string, BinanceSymbolConfigurations>();
        public int MaxLimit { get; set; } = 200;

        /// <summary>Receive closed candles from the WebSocket the moment they close. The REST loop runs either way as the safety net.</summary>
        public bool LiveCandles { get; set; } = true;

    }
}
