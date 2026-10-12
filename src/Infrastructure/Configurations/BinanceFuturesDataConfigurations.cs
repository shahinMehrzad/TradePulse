using Domain.Enums;

namespace Infrastructure.Configurations
{
    /// <summary>
    /// Futures-only market data besides candles. Each section is a dictionary so a symbol can be switched
    /// on or off by editing configuration only; the key is just a readable label.
    /// </summary>
    public class BinanceFuturesDataConfigurations
    {
        public Dictionary<string, FundingRateConfiguration> FundingRates { get; set; } = [];
        public Dictionary<string, OpenInterestConfiguration> OpenInterests { get; set; } = [];
        public Dictionary<string, LiquidationConfiguration> Liquidations { get; set; } = [];
    }

    public class FundingRateConfiguration
    {
        public SymbolEnum Name { get; set; } = SymbolEnum.BTCUSDT;
        public DateTime StartDate { get; set; } = DateTime.UtcNow.AddYears(-1);
    }

    public class OpenInterestConfiguration
    {
        public SymbolEnum Name { get; set; } = SymbolEnum.BTCUSDT;

        /// <summary>Binance supports 5m, 15m, 30m, 1h, 2h, 4h, 6h, 12h and 1d. Binance keeps only ~30 days of history.</summary>
        public TimeFrameEnum Interval { get; set; } = TimeFrameEnum._1H;
    }

    public class LiquidationConfiguration
    {
        public SymbolEnum Name { get; set; } = SymbolEnum.BTCUSDT;
    }
}
