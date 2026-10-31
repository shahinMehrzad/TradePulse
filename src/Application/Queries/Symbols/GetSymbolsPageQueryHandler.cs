using Application.Dtos.Symbols;
using Application.Interfaces;
using Domain.Exceptions;
using Domain.Repositories;

namespace Application.Queries.Symbols;

public readonly record struct GetSymbolsPageQuery(string? Keyword, int PageNumber, int PageSize);

public sealed class GetSymbolsPageQueryHandler(ISymbolCatalogRepository repository) : ICommandHandler<GetSymbolsPageQuery, SymbolPage>
{
    public const int MaxPageSize = 100;

    public async Task<SymbolPage> HandleAsync(GetSymbolsPageQuery request, CancellationToken cancellationToken)
    {
        if (request.PageNumber < 1)
            throw new ConfigurationValidationException("pageNumber must be 1 or more.");
        if (request.PageSize is < 1 or > MaxPageSize)
            throw new ConfigurationValidationException($"pageSize must be between 1 and {MaxPageSize}.");

        var (symbols, total) = await repository.GetSymbolsPage(request.Keyword, (request.PageNumber - 1) * request.PageSize, request.PageSize, cancellationToken);

        // The listings are few (a symbol per exchange market), so the page's share of them is cut from all of them.
        var onPage = symbols.Select(s => s.Id).ToHashSet();
        var markets = (await repository.GetListings(cancellationToken))
            .Where(l => onPage.Contains(l.SymbolId))
            .ToLookup(l => l.SymbolId, l => new SymbolMarket(l.Id, l.Exchange!.Name, l.Market!.Name, l.ExchangeSymbol));

        var items = symbols.Select(s => new SymbolPageItem(s.Id, s.Name, s.Description, [.. markets[s.Id]])).ToList();
        return new SymbolPage(items, request.PageNumber, request.PageSize, total, (int)Math.Ceiling(total / (double)request.PageSize));
    }
}
