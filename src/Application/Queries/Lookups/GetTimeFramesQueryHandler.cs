using Application.Interfaces;
using Domain.Entities;
using Domain.Repositories;

namespace Application.Queries.Lookups
{
    public readonly record struct GetTimeFramesQuery(int MinInterval, int MaxInterval);
    public sealed class GetTimeFramesQueryHandler(ILookupsRepository lookupsRepository) : ICommandHandler<GetTimeFramesQuery, IEnumerable<TimeFrameLookup>>
    {
        private readonly ILookupsRepository _lookupsRepository = lookupsRepository;
        public async Task<IEnumerable<TimeFrameLookup>> HandleAsync(GetTimeFramesQuery request, CancellationToken cancellationToken)
            => await _lookupsRepository.GetTimeFrameListAsync(request.MinInterval, request.MaxInterval, cancellationToken);
    }
}
