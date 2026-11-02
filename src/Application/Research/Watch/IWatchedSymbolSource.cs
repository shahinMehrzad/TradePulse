using Domain.Enums;
using Domain.ValueObjects;

namespace Application.Research.Watch;

/// <summary>Which symbols the watch follows right now. Asked again and again: the answer may change while the watch runs.</summary>
public interface IWatchedSymbolSource
{
    Task<IReadOnlyList<WatchedSymbol>> GetAsync(CancellationToken cancellationToken);
}

/// <summary>A list that never changes (the symbols given on the command line).</summary>
public sealed class FixedWatchedSymbols(IReadOnlyList<WatchedSymbol> symbols) : IWatchedSymbolSource
{
    public Task<IReadOnlyList<WatchedSymbol>> GetAsync(CancellationToken cancellationToken) => Task.FromResult(symbols);
}

/// <summary>The symbols the admin switched on in the panel. An empty list is a valid answer: the watch then waits.</summary>
public sealed class ConfiguredWatchedSymbols(IShockWatchConfigurationRepository repository) : IWatchedSymbolSource
{
    public async Task<IReadOnlyList<WatchedSymbol>> GetAsync(CancellationToken cancellationToken) =>
        (await repository.GetAll(cancellationToken))
        .Where(c => c.IsEnabled)
        .Select(c => new WatchedSymbol((ExchangeEnum)c.SymbolListing!.ExchangeId, (MarketTypeEnum)c.SymbolListing.MarketId, c.SymbolListingId,
            new ExchangeSymbol(c.SymbolListing.ExchangeSymbol)))
        .ToList();
}
