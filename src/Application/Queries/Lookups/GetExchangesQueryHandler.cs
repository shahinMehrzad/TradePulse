using Application.Interfaces;
using Domain.Entities;
using Domain.Repositories;

namespace Application.Queries.Lookups
{
    public readonly record struct GetExchangesQuery(); 
    public sealed class GetExchangesQueryHandler(ILookupsRepository lookupsRepository): ICommandHandler<GetExchangesQuery, IEnumerable<ExchangeLookup>>
    {
        private readonly ILookupsRepository _lookupsRepository = lookupsRepository;
        public async Task<IEnumerable<ExchangeLookup>> HandleAsync(GetExchangesQuery request, CancellationToken cancellationToken)
        {
            return await _lookupsRepository.GetExchangeListAsync(cancellationToken);
        }
    
    }
}
