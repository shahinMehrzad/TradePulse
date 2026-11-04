using Application.Dtos.Symbols;
using Application.Interfaces;
using Application.Research.Watch;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Queries.Symbols;

public readonly record struct GetSymbolDetailQuery(int Id);

public sealed class GetSymbolDetailQueryHandler(ISymbolCatalogRepository repository, ITradedSymbolSource traded) : ICommandHandler<GetSymbolDetailQuery, SymbolDetail>
{
    public async Task<SymbolDetail> HandleAsync(GetSymbolDetailQuery request, CancellationToken cancellationToken)
    {
        var symbol = await repository.GetSymbol(request.Id, cancellationToken)
            ?? throw new ConfigurationNotFoundException("Symbol does not exist.");

        var usage = await repository.GetListingUsage(symbol.Id, cancellationToken);
        var tradedNames = await traded.GetEnabledAsync(cancellationToken);
        var markets = (await repository.GetListings(cancellationToken))
            .Where(l => l.SymbolId == symbol.Id)
            .Select(l =>
            {
                var (series, settings, watches) = usage.GetValueOrDefault(l.Id);
                return new SymbolDetailMarket(l.Id, l.ExchangeId, l.Exchange!.Name, l.MarketId, l.Market!.Name, l.ExchangeSymbol, series, settings, watches,
                    ConfiguredWatchedSymbols.IsTraded(l, tradedNames));
            })
            .ToList();

        return new SymbolDetail(symbol.Id, symbol.Name, symbol.Description, markets);
    }
}
