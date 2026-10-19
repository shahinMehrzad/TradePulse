using Application.Interfaces;
using Domain.Entities;
using Domain.Repositories;

namespace Application.Queries.Lookups
{
    public readonly record struct GetMarketsQuery();
    public sealed class GetMarketsQueryHandler(ILookupsRepository lookupsRepository) : ICommandHandler<GetMarketsQuery, IEnumerable<MarketLookup>>
    {
        private readonly ILookupsRepository _lookupsRepository = lookupsRepository;
        public async Task<IEnumerable<MarketLookup>> HandleAsync(GetMarketsQuery request, CancellationToken cancellationToken)
            => await _lookupsRepository.GetMarketListAsync(cancellationToken);
    }
}
