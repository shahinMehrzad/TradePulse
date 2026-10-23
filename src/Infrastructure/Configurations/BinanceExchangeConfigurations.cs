namespace Infrastructure.Configurations
{
    /// <summary>The Binance adapter's settings.</summary>
    public class BinanceExchangeConfigurations
    {
        /// <summary>The request weight the adapter may use per minute and market. Binance counts it per IP; see <c>BinanceWeightLimiter</c>.</summary>
        public int MaxLimit { get; set; } = 200;
    }
}
