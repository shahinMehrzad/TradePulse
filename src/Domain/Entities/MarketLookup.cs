namespace Domain.Entities
{
    /// <summary>Reference row for <c>MarketTypeEnum</c>. <see cref="Id"/> is the enum's numeric value; rows are only ever created from the enum.</summary>
    public class MarketLookup
    {
        public int Id { get; set; }
        public required string Name { get; set; }
    }
}
