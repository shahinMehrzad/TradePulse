using Domain.Entities;

namespace Domain.Repositories;

/// <summary>The symbols and where each is listed, as the admin manages them.</summary>
public interface ISymbolCatalogRepository
{
    /// <summary>Every listing with its exchange, market and symbol loaded.</summary>
    Task<IReadOnlyList<SymbolListing>> GetListings(CancellationToken cancellationToken);

    Task<SymbolListing?> GetListing(int id, CancellationToken cancellationToken);

    Task<SymbolLookup?> GetSymbol(int id, CancellationToken cancellationToken);

    /// <summary>
    /// One page of symbols by name, and how many there are in all. The keyword (any case) matches the name, the description or
    /// the name an exchange uses for the symbol (<c>btcusdt</c> finds <c>BTC/USDT</c>).
    /// </summary>
    Task<(IReadOnlyList<SymbolLookup> Items, int Total)> GetSymbolsPage(string? keyword, int skip, int take, CancellationToken cancellationToken);

    /// <summary>
    /// For each listing of the symbol that something uses, how many candle series and how many funding rate / liquidation settings
    /// point at it. A listing nothing uses is not in the answer.
    /// </summary>
    Task<IReadOnlyDictionary<int, (int Series, int Settings)>> GetListingUsage(int symbolId, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Exceptions.ConfigurationNotFoundException"/> when it does not exist. The name is never changed: tables are named after the exchange's name, not this one.</summary>
    Task<SymbolLookup> UpdateSymbolDescription(int id, string? description, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Exceptions.ConfigurationConflictException"/> when the name is taken.</summary>
    Task<int> AddSymbol(SymbolLookup symbol, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Exceptions.ConfigurationNotFoundException"/> when it does not exist and <see cref="Exceptions.ConfigurationConflictException"/> when a listing still uses it.</summary>
    Task RemoveSymbol(int id, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Exceptions.ConfigurationConflictException"/> when the symbol is already listed there or the exchange name is taken, <see cref="Exceptions.ConfigurationValidationException"/> for an unknown exchange, market or symbol.</summary>
    Task<int> AddListing(SymbolListing listing, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Exceptions.ConfigurationNotFoundException"/> when it does not exist and <see cref="Exceptions.ConfigurationConflictException"/> when a setting still uses it.</summary>
    Task RemoveListing(int id, CancellationToken cancellationToken);
}
