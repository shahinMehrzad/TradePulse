using Application.Dtos.Lookups;
using Application.Interfaces;
using Domain.Repositories;

namespace Application.Queries.Lookups;

public readonly record struct GetSymbolsQuery(string? Keyword);
public sealed class GetSymbolsQueryHandler(ILookupsRepository lookupsRepository) : ICommandHandler<GetSymbolsQuery, IEnumerable<SymbolsResponseDto>>
{
    private readonly ILookupsRepository _lookupsRepository = lookupsRepository;
    public async Task<IEnumerable<SymbolsResponseDto>> HandleAsync(GetSymbolsQuery request, CancellationToken cancellationToken)
    {
        var res = await _lookupsRepository.GetSymbolListAsync(request.Keyword, cancellationToken);
        return res.SelectMany(x=> new List<SymbolsResponseDto> { new SymbolsResponseDto { Id = x.Id, Name = x.Name, Description = x.Description } });
    }
}
