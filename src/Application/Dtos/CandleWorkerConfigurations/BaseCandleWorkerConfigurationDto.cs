namespace Application.Dtos.CandleWorkerConfigurations;

public class BaseCandleWorkerConfigurationDto
{
    /// <summary>The symbol on an exchange market to collect (see <c>api/admin/symbols/listings</c>).</summary>
    public int SymbolListingId { get; set; }
    public int TimeFrameId { get; set; }
    public DateTime StartDate { get; set; }
    public bool CollectOpenInterest { get; set; } = false;
    public bool CollectMarketStatistics { get; set; } = false;
}
