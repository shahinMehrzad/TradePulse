using Domain.Entities;
using Domain.Repositories;

namespace Application.UnitTests;

internal sealed class FakeLookupsRepository : ILookupsRepository
{
    public List<ExchangeLookup> Exchanges { get; } = [];
    public List<MarketLookup> Markets { get; } = [];
    public List<SymbolLookup> Symbols { get; } = [];
    public List<TimeFrameLookup> TimeFrames { get; } = [];

    /// <summary>The arguments of the last call, so tests can see what the handler passed down.</summary>
    public string? LastKeyword { get; private set; }
    public (int Min, int Max)? LastRange { get; private set; }

    public Task<IEnumerable<ExchangeLookup>> GetExchangeListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IEnumerable<ExchangeLookup>>(Exchanges);

    public Task<IEnumerable<MarketLookup>> GetMarketListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IEnumerable<MarketLookup>>(Markets);

    public Task<IEnumerable<SymbolLookup>> GetSymbolListAsync(string? keyword, CancellationToken cancellationToken = default)
    {
        LastKeyword = keyword;
        return Task.FromResult<IEnumerable<SymbolLookup>>(Symbols);
    }

    public Task<IEnumerable<TimeFrameLookup>> GetTimeFrameListAsync(int minInterval = 0, int maxInterval = int.MaxValue, CancellationToken cancellationToken = default)
    {
        LastRange = (minInterval, maxInterval);
        return Task.FromResult<IEnumerable<TimeFrameLookup>>(TimeFrames);
    }
}
