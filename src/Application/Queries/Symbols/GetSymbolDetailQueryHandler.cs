using Application.Dtos.Symbols;
using Application.Interfaces;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Queries.Symbols;

public readonly record struct GetSymbolDetailQuery(int Id);

public sealed class GetSymbolDetailQueryHandler(ISymbolCatalogRepository repository) : ICommandHandler<GetSymbolDetailQuery, SymbolDetail>
{
    public async Task<SymbolDetail> HandleAsync(GetSymbolDetailQuery request, CancellationToken cancellationToken)
    {
        var symbol = await repository.GetSymbol(request.Id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Symbol does not exist.");

        var usage = await repository.GetListingUsage(symbol.Id, cancellationToken);
        var markets = (await repository.GetListings(cancellationToken))
            .Where(l => l.SymbolId == symbol.Id)
            .Select(l =>
            {
                var (series, settings) = usage.GetValueOrDefault(l.Id);
                return new SymbolDetailMarket(l.Id, l.ExchangeId, l.Exchange!.Name, l.MarketId, l.Market!.Name, l.ExchangeSymbol, series, settings);
            })
            .ToList();

        return new SymbolDetail(symbol.Id, symbol.Name, symbol.Description, markets);
    }
}
