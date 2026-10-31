namespace Application.Dtos.SymbolDataConfigurations;

public class SymbolDataConfigurationDto
{
    /// <summary>The symbol on an exchange market (see <c>api/admin/symbols/listings</c>).</summary>
    public int SymbolListingId { get; set; }
    public bool CollectFundingRate { get; set; }

    /// <summary>UTC. Required when <see cref="CollectFundingRate"/> is on, ignored (and not stored) when it is off.</summary>
    public DateTime? FundingStartDate { get; set; }

    public bool CollectLiquidation { get; set; }
}
