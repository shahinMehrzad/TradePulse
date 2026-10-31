namespace Application.Dtos.Symbols;

/// <summary>A new symbol, independent of any exchange: <c>BTC/USDT</c>.</summary>
public sealed class AddSymbolDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
}

/// <summary>A symbol listed on an exchange market, under the name that exchange uses for it.</summary>
/// <summary>Changes what a symbol is described as. Its name stays: the collector's tables are named after the exchange's name for it.</summary>
public sealed class UpdateSymbolDto
{
    public int Id { get; set; }
    public string? Description { get; set; }
}

/// <summary>One page of the symbol list, with the totals of the (filtered) list.</summary>
public sealed record SymbolPage(IReadOnlyList<SymbolPageItem> Items, int PageNumber, int PageSize, int TotalItems, int TotalPages);

public sealed record SymbolPageItem(int Id, string Name, string? Description, IReadOnlyList<SymbolMarket> Markets);

/// <summary>Where a symbol is listed: one exchange market, under the name that exchange uses for it.</summary>
public sealed record SymbolMarket(int ListingId, string Exchange, string Market, string ExchangeSymbol);

/// <summary>One symbol with every market it is listed on and what uses each listing (a listing that is used cannot be removed).</summary>
public sealed record SymbolDetail(int Id, string Name, string? Description, IReadOnlyList<SymbolDetailMarket> Markets);

public sealed record SymbolDetailMarket(
    int ListingId, int ExchangeId, string Exchange, int MarketId, string Market, string ExchangeSymbol, int SeriesCount, int SettingsCount);

public sealed class AddSymbolListingDto
{
    public int ExchangeId { get; set; }
    public int MarketId { get; set; }
    public int SymbolId { get; set; }

    /// <summary>The exchange's own name, e.g. <c>BTCUSDT</c>.</summary>
    public string? ExchangeSymbol { get; set; }
}

public sealed class SymbolListingListItem
{
    public int Id { get; set; }
    public int ExchangeId { get; set; }
    public string Exchange { get; set; } = "";
    public int MarketId { get; set; }
    public string Market { get; set; } = "";
    public int SymbolId { get; set; }
    public string Symbol { get; set; } = "";
    public string ExchangeSymbol { get; set; } = "";
}
