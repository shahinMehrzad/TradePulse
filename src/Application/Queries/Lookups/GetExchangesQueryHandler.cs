using Application.Dtos.Lookups;
using Application.Interfaces;
using Domain.Repositories;

namespace Application.Queries.Lookups;

public readonly record struct GetExchangesQuery(); 
public sealed class GetExchangesQueryHandler(ILookupsRepository lookupsRepository): ICommandHandler<GetExchangesQuery, IEnumerable<ExchangesResponseDto>>
{
    private readonly ILookupsRepository _lookupsRepository = lookupsRepository;
    public async Task<IEnumerable<ExchangesResponseDto>> HandleAsync(GetExchangesQuery request, CancellationToken cancellationToken)
    {            
        var res = await _lookupsRepository.GetExchangeListAsync(cancellationToken);
        return res.SelectMany(x => new List<ExchangesResponseDto> { new ExchangesResponseDto { Id = x.Id, Name = x.Name } });
    }

}
