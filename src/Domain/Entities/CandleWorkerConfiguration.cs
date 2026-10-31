namespace Domain.Entities
{
    /// <summary>One candle series the admin wants the worker to collect: a symbol listing (exchange + market + symbol) + a time frame.</summary>
    public class CandleWorkerConfiguration
    {
        public int Id { get; set; }
        public int SymbolListingId { get; set; }
        public int TimeFrameId { get; set; }
        public SymbolListing? SymbolListing { get; set; }
        public TimeFrameLookup? TimeFrame { get; set; }

        /// <summary>UTC. Stored as <c>timestamp</c> (no zone) like every other time column.</summary>
        public DateTime StartDate { get; set; }
        public bool CollectOpenInterest { get; set; }
        /// <summary>Also collect the long/short ratio and the taker volume of this series.</summary>
        public bool CollectMarketStatistics { get; set; }
        public DateTime CreateDate { get; set; }
    }
}
