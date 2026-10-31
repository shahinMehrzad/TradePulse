namespace Application.Dtos.Symbols;

/// <summary>A new symbol, independent of any exchange: <c>BTC/USDT</c>.</summary>
public sealed class AddSymbolDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
}

/// <summary>A symbol listed on an exchange market, under the name that exchange uses for it.</summary>
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
