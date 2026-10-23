namespace Domain.Entities
{
    /// <summary>One candle series the admin wants the worker to collect: exchange + market + symbol + time frame.</summary>
    public class CandleWorkerConfiguration
    {
        public int Id { get; set; }
        public int ExchangeId { get; set; }
        public int MarketId { get; set; }
        public int SymbolId { get; set; }
        public int TimeFrameId { get; set; }
        public ExchangeLookup? Exchange { get; set; }
        public MarketLookup? Market { get; set; }
        public SymbolLookup? Symbol { get; set; }
        public TimeFrameLookup? TimeFrame { get; set; }

        /// <summary>UTC. Stored as <c>timestamp</c> (no zone) like every other time column.</summary>
        public DateTime StartDate { get; set; }
        public bool CollectOpenInterest { get; set; }
        public DateTime CreateDate { get; set; }
    }
}
