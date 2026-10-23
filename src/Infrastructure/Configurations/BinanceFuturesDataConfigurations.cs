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
        public Dictionary<string, LiquidationConfiguration> Liquidations { get; set; } = [];
    }

    public class FundingRateConfiguration
    {
        public SymbolEnum Name { get; set; } = SymbolEnum.BTCUSDT;
        public DateTime StartDate { get; set; } = DateTime.UtcNow.AddYears(-1);
    }

    public class LiquidationConfiguration
    {
        public SymbolEnum Name { get; set; } = SymbolEnum.BTCUSDT;
    }
}
