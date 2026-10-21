using Domain.Entities;

namespace Domain.Repositories
{
    public interface ILookupsRepository
    {
        Task<IEnumerable<ExchangeLookup>> GetExchangeListAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<MarketLookup>> GetMarketListAsync(CancellationToken cancellationToken = default);
        Task<IEnumerable<TimeFrameLookup>> GetTimeFrameListAsync(int minInterval = 0, int maxInterval = int.MaxValue, CancellationToken cancellationToken = default);
        Task<IEnumerable<SymbolLookup>> GetSymbolListAsync(string? keyword, CancellationToken cancellationToken = default);
    }
}
