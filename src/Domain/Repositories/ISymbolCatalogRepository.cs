using Domain.Entities;

namespace Domain.Repositories;

/// <summary>The symbols and where each is listed, as the admin manages them.</summary>
public interface ISymbolCatalogRepository
{
    /// <summary>Every listing with its exchange, market and symbol loaded.</summary>
    Task<IReadOnlyList<SymbolListing>> GetListings(CancellationToken cancellationToken);

    Task<SymbolListing?> GetListing(int id, CancellationToken cancellationToken);

    Task<SymbolLookup?> GetSymbol(int id, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Exceptions.ConfigurationConflictException"/> when the name is taken.</summary>
    Task<int> AddSymbol(SymbolLookup symbol, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Exceptions.ConfigurationNotFoundException"/> when it does not exist and <see cref="Exceptions.ConfigurationConflictException"/> when a listing still uses it.</summary>
    Task RemoveSymbol(int id, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Exceptions.ConfigurationConflictException"/> when the symbol is already listed there or the exchange name is taken, <see cref="Exceptions.ConfigurationValidationException"/> for an unknown exchange, market or symbol.</summary>
    Task<int> AddListing(SymbolListing listing, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Exceptions.ConfigurationNotFoundException"/> when it does not exist and <see cref="Exceptions.ConfigurationConflictException"/> when a setting still uses it.</summary>
    Task RemoveListing(int id, CancellationToken cancellationToken);
}
