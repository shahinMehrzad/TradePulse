namespace Application.Dtos.SymbolDataConfigurations;

public class SymbolDataConfigurationDto
{
    public int ExchangeId { get; set; }
    public int MarketId { get; set; }
    public int SymbolId { get; set; }
    public bool CollectFundingRate { get; set; }

    /// <summary>UTC. Required when <see cref="CollectFundingRate"/> is on, ignored (and not stored) when it is off.</summary>
    public DateTime? FundingStartDate { get; set; }

    public bool CollectLiquidation { get; set; }
}
