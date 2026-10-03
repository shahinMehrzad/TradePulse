using Domain.ValueObjects;

namespace Domain.Entities
{
    public class Candle
    {
        public TimeRange TimeRange { get; init; }
        public Price Open { get; private set; }
        public Price High { get; init; }
        public Price Low { get; init; }
        public Price Close { get; init; }
        public decimal Volume { get; init; }
        public decimal QuoteAssetVolume { get; init; }
        public long NumberOfTrades { get; init; }
        public decimal TakerBuyBaseVolume { get; init; }
        public decimal TakerBuyQuoteVolume { get; init; }

        private Candle() { }

        public Candle(
            DateTime openTime,
            decimal openPrice,
            decimal highPrice,
            decimal lowPrice,
            decimal closePrice,
            decimal volume,
            DateTime closeTime,
            decimal quoteAssetVolume,
            long numberOfTrades,
            decimal takerBuyBaseVolume,
            decimal takerBuyQuoteVolume)
        {
            TimeRange = new TimeRange(openTime, closeTime);
            Open = new Price(openPrice);
            High = new Price(highPrice);
            Low = new Price(lowPrice);
            Close = new Price(closePrice);
            Volume = volume;
            QuoteAssetVolume = quoteAssetVolume;
            NumberOfTrades = numberOfTrades;
            TakerBuyBaseVolume = takerBuyBaseVolume;
            TakerBuyQuoteVolume = takerBuyQuoteVolume;
        }
    }
}
