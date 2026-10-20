using Application.Dtos.Lookups;
using Application.Interfaces;
using Domain.Repositories;

namespace Application.Queries.Lookups;

public readonly record struct GetTimeFramesQuery(int MinInterval, int MaxInterval);
public sealed class GetTimeFramesQueryHandler(ILookupsRepository lookupsRepository) : ICommandHandler<GetTimeFramesQuery, IEnumerable<TimeFramesResponseDto>>
{
    private readonly ILookupsRepository _lookupsRepository = lookupsRepository;
    public async Task<IEnumerable<TimeFramesResponseDto>> HandleAsync(GetTimeFramesQuery request, CancellationToken cancellationToken)
    {
        var res = await _lookupsRepository.GetTimeFrameListAsync(request.MinInterval, request.MaxInterval, cancellationToken);
        return res.SelectMany(x => new List<TimeFramesResponseDto> { new TimeFramesResponseDto { Id = x.Id, Name = x.Name, IntervalToSeconds = x.IntervalToSeconds } });
    }
}
