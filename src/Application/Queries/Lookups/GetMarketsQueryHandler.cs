using Application.Dtos.Lookups;
using Application.Interfaces;
using Domain.Repositories;

namespace Application.Queries.Lookups;

public readonly record struct GetMarketsQuery();
public sealed class GetMarketsQueryHandler(ILookupsRepository lookupsRepository) : ICommandHandler<GetMarketsQuery, IEnumerable<MarketsResponseDto>>
{
    private readonly ILookupsRepository _lookupsRepository = lookupsRepository;
    public async Task<IEnumerable<MarketsResponseDto>> HandleAsync(GetMarketsQuery request, CancellationToken cancellationToken)
    {
        var res = await _lookupsRepository.GetMarketListAsync(cancellationToken);
        return res.SelectMany(x => new List<MarketsResponseDto> { new MarketsResponseDto { Id = x.Id, Name = x.Name } });
    }        
}
