namespace Application.Dtos.CandleWorkerConfigurations;

public class BaseCandleWorkerConfigurationDto
{
    public int ExchangeId { get; set; }
    public int MarketId { get; set; }
    public int SymbolId { get; set; }
    public int TimeFrameId { get; set; }
    public DateTime StartDate { get; set; }
}