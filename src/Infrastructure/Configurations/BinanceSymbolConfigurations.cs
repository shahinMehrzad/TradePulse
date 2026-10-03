using Domain.Enums;

namespace Infrastructure.Configurations
{
    public class BinanceSymbolConfigurations
    {
        public SymbolEnum Name { get; set; } = SymbolEnum.BTCUSDT;
        public TimeFrameEnum Interval { get; set; } = TimeFrameEnum._1D;
        public DateTime StartDate { get; set; } = DateTime.UtcNow.AddYears(-1);
        public MarketTypeEnum MarketType { get; set; } = MarketTypeEnum.Spot;
    }
}
