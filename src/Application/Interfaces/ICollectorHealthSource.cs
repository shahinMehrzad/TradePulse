using Application.Dtos.Collector;

namespace Application.Interfaces;

/// <summary>Asks the running collector how it is doing. Never throws for a collector that is down or not set up: that is an answer.</summary>
public interface ICollectorHealthSource
{
    Task<CollectorHealthResponse> GetAsync(CancellationToken cancellationToken);
}
