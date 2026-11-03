using Application.Dtos.ShockWatch;
using Application.Interfaces;
using Application.Research.Watch;
using Domain.Enums;
using Domain.Repositories;

namespace Application.Queries.AdminConfigurations;

public readonly record struct GetShockWatchQuery();

/// <summary>
/// The watch list as the watch sees it: the rows, and the symbols enabled for trading that have no row (watched on their own).
/// A failure to read the trading service fails the query: a list without the traded symbols would look as if they are not watched.
/// </summary>
public sealed class GetShockWatchQueryHandler(
    IShockWatchConfigurationRepository repository, ICandleWorkerConfigurationRepository series, ITradedSymbolSource traded, ISymbolCatalogRepository catalog)
    : ICommandHandler<GetShockWatchQuery, IReadOnlyList<ShockWatchListItem>>
{
    public async Task<IReadOnlyList<ShockWatchListItem>> HandleAsync(GetShockWatchQuery request, CancellationToken cancellationToken)
    {
        var withMinuteCandles = (await series.GetConfigurationList(cancellationToken))
            .Where(c => c.TimeFrameId == (int)TimeFrameEnum._1M)
            .Select(c => c.SymbolListingId)
            .ToHashSet();

        var rows = await repository.GetAll(cancellationToken);
        var tradedNames = await traded.GetEnabledAsync(cancellationToken);
        var tradedListings = tradedNames.Count == 0
            ? []
            : ConfiguredWatchedSymbols.TradedListings(await catalog.GetListings(cancellationToken), tradedNames).ToList();
        var tradedIds = tradedListings.Select(l => l.Id).ToHashSet();

        var items = rows.Select(c => ShockWatchListItem.From(c, withMinuteCandles.Contains(c.SymbolListingId), tradedIds.Contains(c.SymbolListingId))).ToList();
        var decided = rows.Select(c => c.SymbolListingId).ToHashSet();
        items.AddRange(tradedListings.Where(l => !decided.Contains(l.Id)).Select(l => ShockWatchListItem.FromTrading(l, withMinuteCandles.Contains(l.Id))));

        return items.OrderBy(i => i.ExchangeSymbol, StringComparer.Ordinal).ThenBy(i => i.MarketId).ToList();
    }
}
