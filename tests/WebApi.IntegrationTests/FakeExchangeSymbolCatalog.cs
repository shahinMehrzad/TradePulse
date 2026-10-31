using Application.Exchange;
using Domain.Enums;
using Domain.ValueObjects;

namespace WebApi.IntegrationTests;

/// <summary>What "the exchange" lists in these tests: nothing reaches the real Binance.</summary>
public sealed class FakeExchangeSymbolCatalog : IExchangeSymbolCatalog
{
    /// <summary>The names that trade on every market, as the exchange spells them.</summary>
    public HashSet<string> Trading { get; } = new([.. Enum.GetNames<SymbolEnum>(), "1000PEPEUSDT"], StringComparer.Ordinal);

    /// <summary>When set, the exchange cannot be reached.</summary>
    public bool Down { get; set; }

    public int Calls { get; private set; }

    public Task<ExchangeResult<ExchangeSymbol?>> FindTradingAsync(
        ExchangeEnum exchange, MarketTypeEnum market, ExchangeSymbol symbol, CancellationToken cancellationToken)
    {
        Calls++;

        if (Down)
            return Task.FromResult(ExchangeResult<ExchangeSymbol?>.Fail("connection refused"));

        var found = Trading.FirstOrDefault(name => string.Equals(name, symbol.Value, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(ExchangeResult<ExchangeSymbol?>.Ok(found is null ? null : new ExchangeSymbol(found)));
    }
}
