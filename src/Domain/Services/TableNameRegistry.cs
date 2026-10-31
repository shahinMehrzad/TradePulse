using Domain.Enums;

namespace Domain.Services
{
    /// <summary>
    /// The only place table names are built. Names are interpolated into SQL and shared with other services,
    /// so the format is a contract: lowercase, <c>{exchange}_{market}_{symbol}[_{timeframe}]_{kind}</c>.
    /// The symbol part is the exchange's own name for it (<c>BTCUSDT</c>), which now comes from the database: it is
    /// reduced to <c>[a-z0-9]</c> here and anything that cannot be turned into a safe name is refused.
    /// </summary>
    public static class TableNameRegistry
    {
        // Postgres cuts identifiers at 63 bytes; the primary key (pk_) and index (ix_) names derive from the table name.
        private const int MaxIdentifierLength = 63;

        private const string FundingRate = "_fundingrate";
        private const string OpenInterest = "_openinterest";
        private const string LongShortRatio = "_longshortratio";
        private const string TakerVolume = "_takervolume";
        private const string Liquidation = "_liquidation";

        // What the longest derived identifier adds around {exchange}_{market}_{symbol}: the index ix_{table}_event_time
        // of the longest data kind with the longest time frame. If every one of those fits, every table name does.
        private static readonly int LongestAddition =
            "ix_".Length + Enum.GetNames<TimeFrameEnum>().Max(n => n.Length)
            + new[] { FundingRate, OpenInterest, LongShortRatio, TakerVolume, Liquidation }.Max(k => k.Length)
            + "_event_time".Length;

        public static string GetCandleTableName(ExchangeEnum exchange, MarketTypeEnum marketType, SymbolEnum symbol, TimeFrameEnum timeframe) =>
            GetCandleTableName(exchange, marketType, EnumName(symbol), timeframe);

        /// <summary>Candles of a symbol named as the exchange names it, e.g. <c>binance_futures_btcusdt_4h</c>.</summary>
        public static string GetCandleTableName(ExchangeEnum exchange, MarketTypeEnum marketType, string exchangeSymbol, TimeFrameEnum timeframe)
        {
            var prefix = Prefix(exchange, marketType, exchangeSymbol);
            ValidateTimeFrame(timeframe);

            return $"{prefix}{Lower(timeframe)}";
        }

        /// <summary>Funding rate settlements, e.g. <c>binance_futures_btcusdt_fundingrate</c>.</summary>
        public static string GetFundingRateTableName(ExchangeEnum exchange, SymbolEnum symbol) =>
            GetFundingRateTableName(exchange, EnumName(symbol));

        public static string GetFundingRateTableName(ExchangeEnum exchange, string exchangeSymbol) =>
            $"{Prefix(exchange, MarketTypeEnum.Futures, exchangeSymbol)}{FundingRate}";

        /// <summary>Open interest snapshots per period, e.g. <c>binance_futures_btcusdt_1h_openinterest</c>.</summary>
        public static string GetOpenInterestTableName(ExchangeEnum exchange, SymbolEnum symbol, TimeFrameEnum timeframe) =>
            GetOpenInterestTableName(exchange, EnumName(symbol), timeframe);

        public static string GetOpenInterestTableName(ExchangeEnum exchange, string exchangeSymbol, TimeFrameEnum timeframe)
        {
            var prefix = Prefix(exchange, MarketTypeEnum.Futures, exchangeSymbol);
            ValidateTimeFrame(timeframe);

            return $"{prefix}{Lower(timeframe)}{OpenInterest}";
        }

        /// <summary>Long/short ratio of all accounts per period, e.g. <c>binance_futures_btcusdt_1h_longshortratio</c>.</summary>
        public static string GetLongShortRatioTableName(ExchangeEnum exchange, SymbolEnum symbol, TimeFrameEnum timeframe) =>
            GetLongShortRatioTableName(exchange, EnumName(symbol), timeframe);

        public static string GetLongShortRatioTableName(ExchangeEnum exchange, string exchangeSymbol, TimeFrameEnum timeframe)
        {
            var prefix = Prefix(exchange, MarketTypeEnum.Futures, exchangeSymbol);
            ValidateTimeFrame(timeframe);

            return $"{prefix}{Lower(timeframe)}{LongShortRatio}";
        }

        /// <summary>Taker buy and sell volume per period, e.g. <c>binance_futures_btcusdt_1h_takervolume</c>.</summary>
        public static string GetTakerVolumeTableName(ExchangeEnum exchange, SymbolEnum symbol, TimeFrameEnum timeframe) =>
            GetTakerVolumeTableName(exchange, EnumName(symbol), timeframe);

        public static string GetTakerVolumeTableName(ExchangeEnum exchange, string exchangeSymbol, TimeFrameEnum timeframe)
        {
            var prefix = Prefix(exchange, MarketTypeEnum.Futures, exchangeSymbol);
            ValidateTimeFrame(timeframe);

            return $"{prefix}{Lower(timeframe)}{TakerVolume}";
        }

        /// <summary>Forced liquidation orders, e.g. <c>binance_futures_btcusdt_liquidation</c>.</summary>
        public static string GetLiquidationTableName(ExchangeEnum exchange, SymbolEnum symbol) =>
            GetLiquidationTableName(exchange, EnumName(symbol));

        public static string GetLiquidationTableName(ExchangeEnum exchange, string exchangeSymbol) =>
            $"{Prefix(exchange, MarketTypeEnum.Futures, exchangeSymbol)}{Liquidation}";

        /// <summary>
        /// Whether an exchange's name for a symbol can be part of a table name on that exchange market, and the part it becomes
        /// (<c>1000PEPEUSDT</c> gives <c>1000pepeusdt</c>). Separators (<c>- _ . /</c>) are dropped, any other character refuses
        /// the name. Two names of the same exchange market can still give the same part (<c>BTC-USDT</c> and <c>BTCUSDT</c>);
        /// whoever adds a symbol has to compare the parts, the database cannot.
        /// </summary>
        public static bool TryGetSymbolPart(ExchangeEnum exchange, MarketTypeEnum marketType, string? exchangeSymbol, out string part, out string reason)
        {
            part = string.Empty;

            if (!Enum.IsDefined(exchange))
                reason = "unknown exchange";
            else if (!Enum.IsDefined(marketType))
                reason = "unknown market";
            else if (string.IsNullOrWhiteSpace(exchangeSymbol))
                reason = "the symbol has no name";
            else if (!TryReduce(exchangeSymbol, out var reduced))
                reason = "the name may only have letters, digits and - _ . /";
            else if (reduced.Length == 0)
                reason = "the name has no letter or digit";
            else if (Lower(exchange).Length + Lower(marketType).Length + 2 + reduced.Length + LongestAddition > MaxIdentifierLength)
                reason = "the name is too long for a table name";
            else
            {
                part = reduced;
                reason = string.Empty;
                return true;
            }

            return false;
        }

        // Ends with an underscore when followed by a timeframe: enum names such as "_4H" already start with one,
        // so candle tables read "..._btcusdt_4h" and the prefix here is "..._btcusdt".
        private static string Prefix(ExchangeEnum exchange, MarketTypeEnum marketType, string exchangeSymbol)
        {
            if (!Enum.IsDefined(exchange))
                throw new ArgumentException("Invalid Exchange.", nameof(exchange));

            if (!Enum.IsDefined(marketType))
                throw new ArgumentException("Invalid Market type.", nameof(marketType));

            if (!TryGetSymbolPart(exchange, marketType, exchangeSymbol, out var part, out var reason))
                throw new ArgumentException($"Invalid Symbol: {reason}.", "symbol");

            return $"{Lower(exchange)}_{Lower(marketType)}_{part}";
        }

        // A symbol that is not a member of the enum is refused as before; its name is what the exchange has always been called with.
        private static string EnumName(SymbolEnum symbol) =>
            Enum.IsDefined(symbol) ? symbol.ToString() : throw new ArgumentException("Invalid Symbol.", nameof(symbol));

        private static bool TryReduce(string name, out string reduced)
        {
            var chars = new char[name.Length];
            var count = 0;

            foreach (var c in name)
            {
                if (c is >= 'a' and <= 'z' or >= '0' and <= '9')
                    chars[count++] = c;
                else if (c is >= 'A' and <= 'Z')
                    chars[count++] = (char)(c + ('a' - 'A'));
                else if (c is not ('-' or '_' or '.' or '/'))
                {
                    reduced = string.Empty;
                    return false;
                }
            }

            reduced = new string(chars, 0, count);
            return true;
        }

        private static void ValidateTimeFrame(TimeFrameEnum timeframe)
        {
            if (!Enum.IsDefined(timeframe))
                throw new ArgumentException("Invalid Timeframe.", nameof(timeframe));
        }

        private static string Lower<T>(T value) where T : struct, Enum => value.ToString().ToLowerInvariant();
    }
}
