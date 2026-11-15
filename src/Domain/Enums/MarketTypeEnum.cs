namespace Domain.Enums
{
    public enum MarketTypeEnum : byte
    {
        Spot = 1,
        Futures = 2,

        /// <summary>A contract for difference at a broker (forex, metals, indices): no order book, and a market that closes at weekends.</summary>
        Cfd = 3,
    }
}
