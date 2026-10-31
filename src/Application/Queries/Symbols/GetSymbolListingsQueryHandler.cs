using Application.Dtos.Symbols;
using Application.Interfaces;
using Domain.Repositories;

namespace Application.Queries.Symbols;

public readonly record struct GetSymbolListingsQuery;

public sealed class GetSymbolListingsQueryHandler(ISymbolCatalogRepository repository)
    : ICommandHandler<GetSymbolListingsQuery, IReadOnlyList<SymbolListingListItem>>
{
    public async Task<IReadOnlyList<SymbolListingListItem>> HandleAsync(GetSymbolListingsQuery request, CancellationToken cancellationToken) =>
        (await repository.GetListings(cancellationToken)).Select(l => new SymbolListingListItem
        {
            Id = l.Id,
            ExchangeId = l.ExchangeId,
            Exchange = l.Exchange!.Name,
            MarketId = l.MarketId,
            Market = l.Market!.Name,
            SymbolId = l.SymbolId,
            Symbol = l.Symbol!.Name,
            ExchangeSymbol = l.ExchangeSymbol,
        }).ToList();
}
