using Application.Dtos.Symbols;
using Application.Exchange;
using Application.Interfaces;
using Application.Services;
using Domain.Entities;
using Domain.Enums;
using Domain.Exceptions;
using Domain.Repositories;
using Domain.Services;
using Domain.ValueObjects;

namespace Application.Commands.Symbols;

/// <summary>
/// Lists a symbol on an exchange market. Everything that can be checked here is checked before the exchange is asked, and the
/// exchange has the last word: a name it does not list as trading is refused, and when it cannot be asked nothing is saved.
/// </summary>
public sealed class AddSymbolListingCommandHandler(ISymbolCatalogRepository repository, IExchangeSymbolCatalog exchangeCatalog, AuditTrail audit)
    : ICommandHandler<AddSymbolListingDto, int>
{
    public async Task<int> HandleAsync(AddSymbolListingDto request, CancellationToken cancellationToken)
    {
        if (request.ExchangeId == default || request.MarketId == default || request.SymbolId == default)
            throw new ConfigurationValidationException("Exchange, market and symbol are required.");

        var exchangeSymbol = request.ExchangeSymbol?.Trim();
        if (string.IsNullOrEmpty(exchangeSymbol))
            throw new ConfigurationValidationException("ExchangeSymbol is required.");

        // The collector can only serve what has code for it; an exchange row alone is not enough.
        if (!Enum.IsDefined((ExchangeEnum)request.ExchangeId))
            throw new ConfigurationValidationException("The collector has no support for that exchange.");
        if (!Enum.IsDefined((MarketTypeEnum)request.MarketId))
            throw new ConfigurationValidationException("Unknown market.");
        var exchange = (ExchangeEnum)request.ExchangeId;
        var market = (MarketTypeEnum)request.MarketId;

        var symbol = await repository.GetSymbol(request.SymbolId, cancellationToken)
            ?? throw new ConfigurationValidationException("Unknown symbol.");

        if (!TableNameRegistry.TryGetSymbolPart(exchange, market, exchangeSymbol, out var part, out var reason))
            throw new ConfigurationValidationException($"{exchangeSymbol} cannot be used: {reason}.");

        // The database only compares the names as typed; the tables are named from the reduced form.
        foreach (var other in (await repository.GetListings(cancellationToken)).Where(l => l.ExchangeId == request.ExchangeId && l.MarketId == request.MarketId))
        {
            if (string.Equals(other.ExchangeSymbol, exchangeSymbol, StringComparison.OrdinalIgnoreCase))
                throw new ConfigurationConflictException($"{other.ExchangeSymbol} is already listed on {exchange} {market}.");

            if (TableNameRegistry.TryGetSymbolPart(exchange, market, other.ExchangeSymbol, out var otherPart, out _) && otherPart == part)
                throw new ConfigurationConflictException($"{exchangeSymbol} would share its table names with {other.ExchangeSymbol}.");
        }

        var found = await exchangeCatalog.FindTradingAsync(exchange, market, new ExchangeSymbol(exchangeSymbol), cancellationToken);
        if (!found.Success)
            throw new ExchangeUnavailableException($"{exchange} could not be asked whether {exchangeSymbol} exists. Try again in a moment.");
        if (found.Value is not { } listed)
            throw new ConfigurationValidationException($"{exchange} {market} has no trading symbol {exchangeSymbol}.");

        // Kept as the exchange spells it: that is what its API is called with.
        exchangeSymbol = listed.Value;

        var id = await repository.AddListing(
            new SymbolListing { ExchangeId = request.ExchangeId, MarketId = request.MarketId, SymbolId = request.SymbolId, ExchangeSymbol = exchangeSymbol },
            cancellationToken);
        await audit.RecordAsync(AuditAction.Added, AuditEntityTypes.SymbolListing, id, Summary(exchange, market, symbol.Name, exchangeSymbol), null, cancellationToken);
        return id;
    }

    internal static string Summary(ExchangeEnum exchange, MarketTypeEnum market, string symbol, string exchangeSymbol) =>
        $"{exchange} / {market} / {symbol} = {exchangeSymbol}";
}
