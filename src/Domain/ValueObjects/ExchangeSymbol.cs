using Domain.Enums;

namespace Domain.ValueObjects;

/// <summary>
/// A symbol as the exchange itself names it (<c>BTCUSDT</c>). It is what the exchange API is called with and what the table
/// names derive from, and it comes from the database, so it is not a closed set like the old <c>SymbolEnum</c>.
/// <see cref="ToString"/> is the name, so it can be passed to an exchange client as is.
/// </summary>
public readonly record struct ExchangeSymbol
{
    private readonly string? _value;

    public ExchangeSymbol(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        _value = value;
    }

    /// <summary>The exchange's name. Empty only for <c>default</c>.</summary>
    public string Value => _value ?? string.Empty;

    public override string ToString() => Value;

    // Temporary, until SymbolEnum is removed: lets the code and tests that still name a symbol by the enum keep compiling.
    public static implicit operator ExchangeSymbol(SymbolEnum symbol) =>
        Enum.IsDefined(symbol) ? new ExchangeSymbol(symbol.ToString()) : throw new ArgumentException("Invalid Symbol.", nameof(symbol));
}
