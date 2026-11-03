using Application.Research;
using Domain.Entities;

namespace Application.UnitTests;

internal sealed class FakeMarketEventStore : IMarketEventStore
{
    /// <summary>What was handed to <see cref="AddNewAsync"/>, call by call.</summary>
    public List<IReadOnlyList<MarketEvent>> Added { get; } = [];

    /// <summary>How many of them the store says were new.</summary>
    public int NewCount { get; set; }

    public Task<int> AddNewAsync(IReadOnlyList<MarketEvent> events, CancellationToken cancellationToken)
    {
        Added.Add(events);
        return Task.FromResult(NewCount);
    }

    public Task<IReadOnlyList<MarketEvent>> ListAsync(int symbolListingId, DateTime? from, DateTime? to, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}
