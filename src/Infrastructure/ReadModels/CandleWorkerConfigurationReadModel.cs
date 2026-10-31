using Application.Dtos.CandleWorkerConfigurations;
using Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.ReadModels;

public sealed class CandleWorkerConfigurationReadModel(ConfigDbContext context) : EfReadModel<CandleWorkerConfigurationListItem>
{
    public override IQueryable<CandleWorkerConfigurationListItem> Query() =>
        context.CandleWorkerConfigurations.AsNoTracking().Select(c => new CandleWorkerConfigurationListItem
        {
            Id = c.Id,
            SymbolListingId = c.SymbolListingId,
            ExchangeSymbol = c.SymbolListing!.ExchangeSymbol,
            ExchangeId = c.SymbolListing.ExchangeId,
            Exchange = c.SymbolListing.Exchange!.Name,
            MarketId = c.SymbolListing.MarketId,
            Market = c.SymbolListing.Market!.Name,
            SymbolId = c.SymbolListing.SymbolId,
            Symbol = c.SymbolListing.Symbol!.Name,
            SymbolDescription = c.SymbolListing.Symbol.Description,
            TimeFrameId = c.TimeFrameId,
            TimeFrame = c.TimeFrame!.Name,
            IntervalSeconds = c.TimeFrame.IntervalToSeconds,
            StartDate = c.StartDate,
            CollectOpenInterest = c.CollectOpenInterest,
            CollectMarketStatistics = c.CollectMarketStatistics,
            CreateDate = c.CreateDate,
        });
}
