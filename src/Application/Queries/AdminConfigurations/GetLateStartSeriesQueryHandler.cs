using Application.Dtos.CandleWorkerConfigurations;
using Application.Interfaces;
using Application.Services;

namespace Application.Queries.AdminConfigurations;

public readonly record struct GetLateStartSeriesQuery();

public sealed class GetLateStartSeriesQueryHandler(LateStartFinder finder)
    : ICommandHandler<GetLateStartSeriesQuery, IReadOnlyList<LateStartSeries>>
{
    public Task<IReadOnlyList<LateStartSeries>> HandleAsync(GetLateStartSeriesQuery request, CancellationToken cancellationToken)
        => finder.FindAsync(cancellationToken);
}
