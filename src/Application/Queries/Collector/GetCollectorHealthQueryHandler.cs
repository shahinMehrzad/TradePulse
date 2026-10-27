using Application.Dtos.Collector;
using Application.Interfaces;

namespace Application.Queries.Collector;

public readonly record struct GetCollectorHealthQuery();

public sealed class GetCollectorHealthQueryHandler(ICollectorHealthSource source)
    : ICommandHandler<GetCollectorHealthQuery, CollectorHealthResponse>
{
    public Task<CollectorHealthResponse> HandleAsync(GetCollectorHealthQuery request, CancellationToken cancellationToken)
        => source.GetAsync(cancellationToken);
}
