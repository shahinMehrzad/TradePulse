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
            ExchangeId = c.ExchangeId,
            Exchange = c.Exchange!.Name,
            MarketId = c.MarketId,
            Market = c.Market!.Name,
            SymbolId = c.SymbolId,
            Symbol = c.Symbol!.Name,
            SymbolDescription = c.Symbol.Description,
            TimeFrameId = c.TimeFrameId,
            TimeFrame = c.TimeFrame!.Name,
            IntervalSeconds = c.TimeFrame.IntervalToSeconds,
            StartDate = c.StartDate,
            CollectOpenInterest = c.CollectOpenInterest,
            CollectMarketStatistics = c.CollectMarketStatistics,
            CreateDate = c.CreateDate,
        });
}
