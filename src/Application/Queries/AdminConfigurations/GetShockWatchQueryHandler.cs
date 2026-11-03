using Application.Dtos.ShockWatch;
using Application.Interfaces;
using Application.Research.Watch;
using Domain.Enums;
using Domain.Repositories;

namespace Application.Queries.AdminConfigurations;

public readonly record struct GetShockWatchQuery();

public sealed class GetShockWatchQueryHandler(IShockWatchConfigurationRepository repository, ICandleWorkerConfigurationRepository series)
    : ICommandHandler<GetShockWatchQuery, IReadOnlyList<ShockWatchListItem>>
{
    public async Task<IReadOnlyList<ShockWatchListItem>> HandleAsync(GetShockWatchQuery request, CancellationToken cancellationToken)
    {
        var withMinuteCandles = (await series.GetConfigurationList(cancellationToken))
            .Where(c => c.TimeFrameId == (int)TimeFrameEnum._1M)
            .Select(c => c.SymbolListingId)
            .ToHashSet();

        return (await repository.GetAll(cancellationToken))
            .Select(c => ShockWatchListItem.From(c, withMinuteCandles.Contains(c.SymbolListingId)))
            .ToList();
    }
}
