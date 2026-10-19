using Application.Interfaces;
using Domain.Entities;
using Domain.Repositories;

namespace Application.Queries.Lookups
{
    public readonly record struct GetSymbolsQuery(string? Keyword);
    public sealed class GetSymbolsQueryHandler(ILookupsRepository lookupsRepository) : ICommandHandler<GetSymbolsQuery, IEnumerable<SymbolLookup>>
    {
        private readonly ILookupsRepository _lookupsRepository = lookupsRepository;
        public async Task<IEnumerable<SymbolLookup>> HandleAsync(GetSymbolsQuery request, CancellationToken cancellationToken)
            => await _lookupsRepository.GetSymbolListAsync(request.Keyword, cancellationToken);
    }
}
