using Domain.Entities;
using Domain.Enums;
using Domain.Services;

namespace Application.Queries.Dashboard;

/// <summary>Which listing a dashboard route names. The symbol in the route is the table-name part of the exchange's name (btcusdt).</summary>
internal static class DashboardListings
{
    public static bool Matches(SymbolListing? listing, ExchangeEnum exchange, MarketTypeEnum market, string symbolPart) =>
        listing is not null && listing.ExchangeId == (int)exchange && listing.MarketId == (int)market
        && TableNameRegistry.TryGetSymbolPart(exchange, market, listing.ExchangeSymbol, out var part, out _)
        && part == symbolPart;
}
