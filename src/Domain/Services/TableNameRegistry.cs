using Domain.Enums;

namespace Domain.Services
{
    /// <summary>
    /// The only place table names are built. Names are interpolated into SQL and shared with other services,
    /// so the format is a contract: lowercase, <c>{exchange}_{market}_{symbol}[_{timeframe}]_{kind}</c>.
    /// </summary>
    public static class TableNameRegistry
    {
        public static string GetCandleTableName(ExchangeEnum exchange, MarketTypeEnum marketType, SymbolEnum symbol, TimeFrameEnum timeframe)
        {
            var prefix = Prefix(exchange, marketType, symbol);
            ValidateTimeFrame(timeframe);

            return $"{prefix}{Lower(timeframe)}";
        }

        /// <summary>Funding rate settlements, e.g. <c>binance_futures_btcusdt_fundingrate</c>.</summary>
        public static string GetFundingRateTableName(ExchangeEnum exchange, SymbolEnum symbol) =>
            $"{Prefix(exchange, MarketTypeEnum.Futures, symbol)}_fundingrate";

        /// <summary>Open interest snapshots per period, e.g. <c>binance_futures_btcusdt_1h_openinterest</c>.</summary>
        public static string GetOpenInterestTableName(ExchangeEnum exchange, SymbolEnum symbol, TimeFrameEnum timeframe)
        {
            var prefix = Prefix(exchange, MarketTypeEnum.Futures, symbol);
            ValidateTimeFrame(timeframe);

            return $"{prefix}{Lower(timeframe)}_openinterest";
        }

        /// <summary>Forced liquidation orders, e.g. <c>binance_futures_btcusdt_liquidation</c>.</summary>
        public static string GetLiquidationTableName(ExchangeEnum exchange, SymbolEnum symbol) =>
            $"{Prefix(exchange, MarketTypeEnum.Futures, symbol)}_liquidation";

        // Ends with an underscore when followed by a timeframe: enum names such as "_4H" already start with one,
        // so candle tables read "..._btcusdt_4h" and the prefix here is "..._btcusdt".
        private static string Prefix(ExchangeEnum exchange, MarketTypeEnum marketType, SymbolEnum symbol)
        {
            if (!Enum.IsDefined(exchange))
                throw new ArgumentException("Invalid Exchange.", nameof(exchange));

            if (!Enum.IsDefined(marketType))
                throw new ArgumentException("Invalid Market type.", nameof(marketType));

            if (!Enum.IsDefined(symbol))
                throw new ArgumentException("Invalid Symbol.", nameof(symbol));

            return $"{Lower(exchange)}_{Lower(marketType)}_{Lower(symbol)}";
        }

        private static void ValidateTimeFrame(TimeFrameEnum timeframe)
        {
            if (!Enum.IsDefined(timeframe))
                throw new ArgumentException("Invalid Timeframe.", nameof(timeframe));
        }

        private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
    }
}
