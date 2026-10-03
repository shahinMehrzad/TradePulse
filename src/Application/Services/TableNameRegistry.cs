using Domain.Enums;

namespace Application.Services
{
    public static class TableNameRegistry
    {
        public static string GetCandleTableName(ExchangeEnum exchange, MarketTypeEnum marketType, SymbolEnum symbol, TimeFrameEnum timeframe)
        {
            if (!Enum.IsDefined<ExchangeEnum>(exchange))
                throw new ArgumentException("Invalid Market type.", nameof(marketType));

            if (!Enum.IsDefined<MarketTypeEnum>(marketType))
                throw new ArgumentException("Invalid Market type.", nameof(marketType));

            if (!Enum.IsDefined<SymbolEnum>(symbol))
                throw new ArgumentException("Invalid Symbol.", nameof(symbol));

            if (!Enum.IsDefined<TimeFrameEnum>(timeframe))
                throw new ArgumentException("Invalid Timeframe.", nameof(timeframe));

            var cleanMarketType = marketType.ToString().ToLowerInvariant();
            var cleanSymbol = symbol.ToString().ToLowerInvariant();
            var cleanTimeframe = timeframe.ToString().ToLowerInvariant();
            var cleanExchangeName = exchange.ToString().ToLowerInvariant();

            return $"{cleanExchangeName}_{cleanMarketType}_{cleanSymbol}{cleanTimeframe}";
        }
    }
}
