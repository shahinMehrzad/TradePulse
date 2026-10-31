using Domain.Entities;

namespace Application.Dtos.SymbolDataConfigurations;

/// <summary>One row of the funding rate and liquidation settings, with the listing spelled out (ids and the exchange's own name).</summary>
public sealed class SymbolDataConfigurationListItem
{
    public int Id { get; set; }
    public int SymbolListingId { get; set; }
    public int ExchangeId { get; set; }
    public int MarketId { get; set; }
    public int SymbolId { get; set; }

    /// <summary>The name the exchange uses for the symbol, e.g. <c>BTCUSDT</c>.</summary>
    public string ExchangeSymbol { get; set; } = "";
    public bool CollectFundingRate { get; set; }

    /// <summary>UTC.</summary>
    public DateTime? FundingStartDate { get; set; }
    public bool CollectLiquidation { get; set; }

    /// <param name="configuration">With its <see cref="SymbolDataConfiguration.SymbolListing"/> loaded.</param>
    public static SymbolDataConfigurationListItem From(SymbolDataConfiguration configuration) => new()
    {
        Id = configuration.Id,
        SymbolListingId = configuration.SymbolListingId,
        ExchangeId = configuration.SymbolListing!.ExchangeId,
        MarketId = configuration.SymbolListing.MarketId,
        SymbolId = configuration.SymbolListing.SymbolId,
        ExchangeSymbol = configuration.SymbolListing.ExchangeSymbol,
        CollectFundingRate = configuration.CollectFundingRate,
        FundingStartDate = configuration.FundingStartDate,
        CollectLiquidation = configuration.CollectLiquidation,
    };
}
