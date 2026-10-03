namespace Infrastructure.Configurations
{
    public class BinanceExchangeConfigurations
    {
        public Dictionary<string, BinanceSymbolConfigurations> Symbols { get; set; } = new Dictionary<string, BinanceSymbolConfigurations>();
        public int MaxLimit { get; set; } = 200;

    }
}
