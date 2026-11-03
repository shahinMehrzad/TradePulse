using Domain.Entities;

namespace Application.Research;

/// <summary>
/// Where the events found in the market data are kept. Lives here, not with the repositories of the market data, because it belongs to
/// the research schema and not to the shared contract the collector and the ML project read.
/// </summary>
public interface IMarketEventStore
{
    /// <summary>
    /// Stores the events (with their reactions) that are not stored yet. An event is the same as a stored one when listing, detector
    /// version and start are. Returns how many were new.
    /// </summary>
    Task<int> AddNewAsync(IReadOnlyList<MarketEvent> events, CancellationToken cancellationToken);

    /// <summary>The events of a listing that started in the range, oldest first, with their reactions.</summary>
    Task<IReadOnlyList<MarketEvent>> ListAsync(int symbolListingId, DateTime? from, DateTime? to, CancellationToken cancellationToken);
}
